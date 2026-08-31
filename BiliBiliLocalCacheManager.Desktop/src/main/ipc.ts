import { BrowserWindow, app, dialog, ipcMain, shell, type IpcMainInvokeEvent } from 'electron';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import type {
  AppSettings,
  ArtifactCleanupResult,
  CacheDetails,
  CacheDetailsRequest,
  CachePage,
  DesktopInfo,
  HostProgress,
  JsonObject,
  PlayerPreference,
  SearchRequest,
  SelectionTarget,
  StorageSnapshot,
  TrashEntry,
} from '../shared/contracts';
import { DEFAULT_CACHE_PAGE_SIZE, MAXIMUM_CACHE_PAGE_SIZE } from '../shared/contracts';
import { channels } from '../shared/channels';
import { DesktopHostBridge, type HostCall } from './host-bridge';
import { isRecord } from './protocol';
import {
  validateCacheDetails,
  validateCachePage,
  validateHostHealth,
  validateInitialState,
  validateScanResult,
} from './host-contract-validation';
import { packagedRendererUrl } from './renderer-protocol';

const allowedSettingKeys = new Set<keyof AppSettings>([
  'rootPath', 'rememberRootPath', 'scanOnStartup', 'includeIncomplete', 'keyword', 'splitKeywords', 'anyKeywords',
  'includePartName', 'includeOwnerName', 'includeBvid', 'includeAvid',
  'caseSensitive', 'matchMode', 'playerPreference',
  'transcodeCacheRetentionDays', 'transcodeCacheMaxSizeGigabytes',
]);

export function registerIpc(bridge: DesktopHostBridge, getWindow: () => BrowserWindow | null): () => void {
  type SenderState = {
    sender: IpcMainInvokeEvent['sender'];
    calls: Map<string, HostCall<unknown>>;
    detailCallIds: Set<string>;
    onDestroyed(): void;
  };
  const activeRequests = new Map<number, SenderState>();
  const handlers: Array<[string, (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown]> = [];
  const handle = (channel: string, listener: (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown) => {
    handlers.push([channel, listener]);
    ipcMain.handle(channel, listener);
  };
  const host = <T>(method: string, params: JsonObject = {}, timeoutMs?: number) => bridge.call<T>(method, params, timeoutMs);
  const track = async <T>(event: IpcMainInvokeEvent, call: HostCall<T>, kind?: 'cache-details'): Promise<T> => {
    const sender = event.sender;
    const senderId = sender.id;
    if (sender.isDestroyed()) {
      call.cancel();
      return call.promise;
    }
    let state = activeRequests.get(senderId);
    if (!state) {
      const onDestroyed = () => {
        const current = activeRequests.get(senderId);
        if (!current) return;
        activeRequests.delete(senderId);
        for (const activeCall of current.calls.values()) activeCall.cancel();
      };
      state = { sender, calls: new Map<string, HostCall<unknown>>(), detailCallIds: new Set<string>(), onDestroyed };
      activeRequests.set(senderId, state);
      sender.once('destroyed', onDestroyed);
    }
    state.calls.set(call.id, call as HostCall<unknown>);
    if (kind === 'cache-details') state.detailCallIds.add(call.id);
    try {
      return await call.promise;
    } finally {
      const current = activeRequests.get(senderId);
      current?.calls.delete(call.id);
      current?.detailCallIds.delete(call.id);
      if (current?.calls.size === 0) {
        current.sender.removeListener('destroyed', current.onDestroyed);
        activeRequests.delete(senderId);
      }
    }
  };

  handle(channels.health, async (event) => {
    assertTrusted(event);
    return validateHostHealth(await track(event, host<unknown>('health', {}, 15_000)));
  });
  handle(channels.initialState, async (event) => {
    assertTrusted(event);
    return validateInitialState(await track(event, host<unknown>('initialState')));
  });
  handle(channels.settingsGet, (event) => {
    assertTrusted(event);
    return track(event, host<AppSettings>('settings.get'));
  });
  handle(channels.settingsUpdate, (event, patch) => {
    assertTrusted(event);
    return track(event, host<AppSettings>('settings.update', validateSettingsPatch(patch) as unknown as JsonObject));
  });
  handle(channels.chooseRoot, async (event, defaultPath) => {
    assertTrusted(event);
    const parent = BrowserWindow.fromWebContents(event.sender) ?? getWindow() ?? undefined;
    const options = {
      title: '选择 B 站缓存根目录',
      defaultPath: optionalPath(defaultPath),
      properties: ['openDirectory', 'createDirectory'] as Array<'openDirectory' | 'createDirectory'>,
    };
    const result = parent ? await dialog.showOpenDialog(parent, options) : await dialog.showOpenDialog(options);
    return result.canceled ? null : result.filePaths[0] ?? null;
  });
  handle(channels.scan, async (event, options) => {
    assertTrusted(event);
    if (!isRecord(options)) throw new TypeError('扫描参数必须是对象。');
    const request = {
      rootPath: assertPath(options.rootPath, 'rootPath'),
      includeIncomplete: assertBoolean(options.includeIncomplete, 'includeIncomplete'),
      ...(options.persistSettings === undefined
        ? {}
        : { persistSettings: assertBoolean(options.persistSettings, 'persistSettings') }),
      offset: options.offset === undefined ? 0 : assertInteger(options.offset, 'offset', 0, 2_147_483_647),
      pageSize: options.pageSize === undefined ? DEFAULT_CACHE_PAGE_SIZE : assertInteger(options.pageSize, 'pageSize', 1, MAXIMUM_CACHE_PAGE_SIZE),
    };
    const result = validateScanResult(await track(event, host<unknown>('scan', request)));
    assertPageMatchesRequest(result, request, 'scan');
    return result;
  });
  handle(channels.cancel, async (event) => {
    assertTrusted(event);
    const calls = [...(activeRequests.get(event.sender.id)?.calls.values() ?? [])];
    let cancelled = false;
    for (const call of calls) cancelled = call.cancel() || cancelled;
    return cancelled;
  });
  handle(channels.search, async (event, request) => {
    assertTrusted(event);
    const validatedRequest = validateSearchRequest(request);
    const result = validateCachePage(await track(event, host<unknown>('search', validatedRequest as unknown as JsonObject)));
    assertPageMatchesRequest(result, validatedRequest, 'search', true);
    return result;
  });
  handle(channels.cacheDetails, async (event, request) => {
    assertTrusted(event);
    const validatedRequest = validateCacheDetailsRequest(request);
    const result = validateCacheDetails(await track(
      event,
      host<unknown>('cache.details', validatedRequest as unknown as JsonObject),
      'cache-details',
    ));
    assertPageMatchesRequest(result, validatedRequest, 'cache.details', true);
    if (result.avid !== validatedRequest.avid) throw new TypeError('Desktop Host 返回的 cache.details.avid 与请求不一致。');
    return result;
  });
  handle(channels.cacheDetailsCancel, async (event) => {
    assertTrusted(event);
    const state = activeRequests.get(event.sender.id);
    if (!state) return false;
    let cancelled = false;
    for (const requestId of [...state.detailCallIds]) {
      cancelled = state.calls.get(requestId)?.cancel() === true || cancelled;
    }
    return cancelled;
  });
  handle(channels.storageGet, (event, rootPath) => {
    assertTrusted(event);
    return track(event, host<StorageSnapshot>(
      'storage.get',
      rootPath === undefined || rootPath === '' ? {} : { rootPath: assertPath(rootPath, 'rootPath') },
    ));
  });
  handle(channels.artifactsCleanup, (event) => {
    assertTrusted(event);
    return track(event, host<ArtifactCleanupResult>('artifacts.cleanup'));
  });
  handle(channels.artifactsClear, async (event) => {
    assertTrusted(event);
    const parent = BrowserWindow.fromWebContents(event.sender) ?? getWindow() ?? undefined;
    const options = {
      type: 'warning' as const,
      title: '清空转码缓存',
      message: '确定要清空全部受管转码缓存吗？',
      detail: '此操作不会删除 B 站原始缓存；正在生成且被锁定的产物可能无法删除，结果会显示失败数量。',
      buttons: ['清空转码缓存', '取消'],
      defaultId: 1,
      cancelId: 1,
      noLink: true,
    };
    const confirmation = parent
      ? await dialog.showMessageBox(parent, options)
      : await dialog.showMessageBox(options);
    if (confirmation.response !== 0) return null;
    return track(event, host<ArtifactCleanupResult>('artifacts.clear', { confirmed: true }));
  });
  handle(channels.artifactsOpen, async (event) => {
    assertTrusted(event);
    const storage = await track(event, host<StorageSnapshot>('storage.get'));
    const managedPath = storage.transcodeCache.path;
    if (!managedPath || !path.isAbsolute(managedPath)) {
      throw new Error('Desktop Host 未返回有效的受管转码缓存目录。');
    }

    await mkdir(managedPath, { recursive: true });
    const openError = await shell.openPath(managedPath);
    if (openError) throw new Error(`无法打开转码缓存目录：${openError}`);
    return true;
  });
  handle(channels.trashMove, (event, rootPath, avids) => {
    assertTrusted(event);
    return track(event, host<{ moved: string[]; failed: string[] }>('trash.move', {
      rootPath: assertPath(rootPath, 'rootPath'),
      avids: validateNonEmptyStringArray(avids, 'avids'),
    }));
  });
  handle(channels.trashList, (event, rootPath) => {
    assertTrusted(event);
    return track(event, host<TrashEntry[]>('trash.list', { rootPath: assertPath(rootPath, 'rootPath') }));
  });
  handle(channels.trashRestore, (event, rootPath, entryIds) => {
    assertTrusted(event);
    return track(event, host<{ restored: string[]; failed: string[] }>('trash.restore', {
      rootPath: assertPath(rootPath, 'rootPath'),
      entryIds: validateNonEmptyStringArray(entryIds, 'entryIds'),
    }));
  });
  handle(channels.trashPurge, async (event, rootPath, entryIds) => {
    assertTrusted(event);
    const validatedRoot = assertPath(rootPath, 'rootPath');
    const validatedIds = validateNonEmptyStringArray(entryIds, 'entryIds', 10_000);
    const parent = BrowserWindow.fromWebContents(event.sender) ?? getWindow() ?? undefined;
    const options = {
      type: 'warning' as const,
      title: '永久清空应用回收站',
      message: '确定要永久删除应用回收站中的缓存吗？',
      detail: '此操作无法撤销。将清理 ' + validatedRoot + ' 中界面列出的 ' + validatedIds.length + ' 项；Host 会再次核对目录和完整条目集合。',
      buttons: ['永久删除', '取消'],
      defaultId: 1,
      cancelId: 1,
      noLink: true,
    };
    const confirmation = parent
      ? await dialog.showMessageBox(parent, options)
      : await dialog.showMessageBox(options);
    if (confirmation.response !== 0) return { purged: [], failed: [] };
    return track(event, host<{ purged: string[]; failed: string[] }>('trash.purge', {
      rootPath: validatedRoot,
      entryIds: validatedIds,
      confirmed: true,
    }));
  });
  handle(channels.play, async (event, rootPath, targets, playerPreference, includeIncomplete) => {
    assertTrusted(event);
    const call = host<{ queued: number }>('play', {
      rootPath: assertPath(rootPath, 'rootPath'),
      targets: validateTargets(targets) as never,
      playerPreference: validatePlayer(playerPreference),
      includeIncomplete: assertBoolean(includeIncomplete, 'includeIncomplete'),
    });
    return track(event, call);
  });
  handle(channels.exportMedia, async (event, rootPath, targets, suggestedName, includeIncomplete) => {
    assertTrusted(event);
    const validatedRoot = assertPath(rootPath, 'rootPath');
    const validatedTargets = validateTargets(targets);
    const outputPath = await chooseExportDestination(
      event,
      getWindow,
      suggestedName,
      requiresExportDirectory(validatedTargets) ? 'media-directory' : 'media-file',
    );
    if (!outputPath) return null;
    const call = host<{ outputPath: string }>('export', {
      rootPath: validatedRoot,
      targets: validatedTargets as never,
      outputPath,
      includeIncomplete: assertBoolean(includeIncomplete, 'includeIncomplete'),
    });
    return track(event, call);
  });
  handle(channels.exportDiagnostics, async (event, suggestedName, rootPath) => {
    assertTrusted(event);
    const outputPath = await chooseExportDestination(event, getWindow, suggestedName, 'diagnostics');
    if (!outputPath) return null;
    const call = host<{ outputPath: string }>('diagnostics.export', {
      outputPath,
      ...(rootPath === undefined || rootPath === '' ? {} : { rootPath: assertPath(rootPath, 'rootPath') }),
    });
    return track(event, call);
  });
  handle(channels.desktopInfo, (event): DesktopInfo => {
    assertTrusted(event);
    return {
      appVersion: app.getVersion(),
      electronVersion: process.versions.electron,
      chromiumVersion: process.versions.chrome,
      nodeVersion: process.versions.node,
      platform: process.platform as DesktopInfo['platform'],
      arch: process.arch as DesktopInfo['arch'],
      displayBackend: process.platform === 'linux' ? 'x11' : 'win32',
    };
  });

  const onEvent = (name: string, payload: unknown) => {
    if (name !== 'progress' || !isRecord(payload)) return;
    getWindow()?.webContents.send(channels.progress, payload as unknown as HostProgress);
  };
  const onUnavailable = (message: string) => getWindow()?.webContents.send(channels.unavailable, message);
  bridge.on('event', onEvent);
  bridge.on('unavailable', onUnavailable);

  return () => {
    for (const [channel] of handlers) ipcMain.removeHandler(channel);
    for (const state of activeRequests.values()) {
      state.sender.removeListener('destroyed', state.onDestroyed);
      for (const call of state.calls.values()) call.cancel();
    }
    activeRequests.clear();
    bridge.off('event', onEvent);
    bridge.off('unavailable', onUnavailable);
  };
}

async function chooseExportDestination(
  event: IpcMainInvokeEvent,
  getWindow: () => BrowserWindow | null,
  suggestedName: unknown,
  kind: 'media-file' | 'media-directory' | 'diagnostics',
): Promise<string | null> {
  const safeName = safeFileName(assertString(suggestedName, 'suggestedName', 160));
  const parent = BrowserWindow.fromWebContents(event.sender) ?? getWindow() ?? undefined;
  if (kind === 'media-directory') {
    const options = {
      title: '选择 MP4 导出目录',
      defaultPath: app.getPath('downloads'),
      buttonLabel: '选择文件夹',
      properties: ['openDirectory', 'createDirectory'] as Array<'openDirectory' | 'createDirectory'>,
    };
    const result = parent
      ? await dialog.showOpenDialog(parent, options)
      : await dialog.showOpenDialog(options);
    return result.canceled || !result.filePaths[0] ? null : path.resolve(result.filePaths[0]);
  }
  const options = {
    title: kind === 'media-file' ? '导出 MP4' : '导出诊断报告',
    defaultPath: path.join(app.getPath('downloads'), safeName),
    filters: kind === 'media-file'
      ? [{ name: 'MP4 视频', extensions: ['mp4'] }]
      : [{ name: 'ZIP 诊断包', extensions: ['zip'] }],
  };
  const result = parent
    ? await dialog.showSaveDialog(parent, options)
    : await dialog.showSaveDialog(options);
  return result.canceled || !result.filePath ? null : path.resolve(result.filePath);
}

function requiresExportDirectory(targets: SelectionTarget[]): boolean {
  if (targets.length !== 1) return true;
  const pageIndexes = targets[0].pageIndexes;
  return pageIndexes === undefined || new Set(pageIndexes).size !== 1;
}

function assertTrusted(event: IpcMainInvokeEvent): void {
  const frame = event.senderFrame;
  if (!frame || frame !== frame.top) throw new Error('拒绝来自子框架的 IPC 调用。');
  const url = frame.url;
  const trusted = url === 'http://127.0.0.1:5173/' || url === packagedRendererUrl;
  if (!trusted) throw new Error('拒绝来自非可信页面的 IPC 调用。');
}

function validateSettingsPatch(value: unknown): Partial<AppSettings> {
  if (!isRecord(value)) throw new TypeError('设置补丁必须是对象。');
  const patch: Record<string, unknown> = {};
  for (const [key, item] of Object.entries(value)) {
    if (!allowedSettingKeys.has(key as keyof AppSettings)) throw new TypeError(`不允许修改设置：${key}`);
    if (key === 'rootPath' || key === 'keyword') patch[key] = assertString(item, key, key === 'rootPath' ? 32_768 : 500);
    else if (key === 'matchMode') {
      if (item !== 'contains' && item !== 'prefix' && item !== 'exact') throw new TypeError('无效匹配模式。');
      patch[key] = item;
    } else if (key === 'playerPreference') patch[key] = validatePlayer(item);
    else if (key === 'transcodeCacheRetentionDays') patch[key] = assertInteger(item, key, 1, 1825);
    else if (key === 'transcodeCacheMaxSizeGigabytes') patch[key] = assertInteger(item, key, 1, 128);
    else patch[key] = assertBoolean(item, key);
  }
  return patch as Partial<AppSettings>;
}

function validateSearchRequest(value: unknown): SearchRequest {
  if (!isRecord(value)) throw new TypeError('搜索参数必须是对象。');
  const matchMode = value.matchMode;
  if (matchMode !== 'contains' && matchMode !== 'prefix' && matchMode !== 'exact') throw new TypeError('无效匹配模式。');
  return {
    indexToken: assertNonEmptyString(value.indexToken, 'indexToken', 128),
    offset: assertInteger(value.offset, 'offset', 0, 2_147_483_647),
    pageSize: assertInteger(value.pageSize, 'pageSize', 1, MAXIMUM_CACHE_PAGE_SIZE),
    keyword: assertString(value.keyword, 'keyword', 500),
    matchMode,
    splitKeywords: assertBoolean(value.splitKeywords, 'splitKeywords'),
    anyKeywords: assertBoolean(value.anyKeywords, 'anyKeywords'),
    includePartName: assertBoolean(value.includePartName, 'includePartName'),
    includeOwnerName: assertBoolean(value.includeOwnerName, 'includeOwnerName'),
    includeBvid: assertBoolean(value.includeBvid, 'includeBvid'),
    includeAvid: assertBoolean(value.includeAvid, 'includeAvid'),
    caseSensitive: assertBoolean(value.caseSensitive, 'caseSensitive'),
  };
}

function validateCacheDetailsRequest(value: unknown): CacheDetailsRequest {
  if (!isRecord(value)) throw new TypeError('缓存详情参数必须是对象。');
  return {
    indexToken: assertNonEmptyString(value.indexToken, 'indexToken', 128),
    avid: assertNonEmptyString(value.avid, 'avid', 64),
    offset: value.offset === undefined ? 0 : assertInteger(value.offset, 'offset', 0, 2_147_483_647),
    pageSize: value.pageSize === undefined
      ? DEFAULT_CACHE_PAGE_SIZE
      : assertInteger(value.pageSize, 'pageSize', 1, MAXIMUM_CACHE_PAGE_SIZE),
  };
}

function assertPageMatchesRequest(
  page: Pick<CachePage, 'indexToken' | 'offset' | 'pageSize'> | Pick<CacheDetails, 'indexToken' | 'offset' | 'pageSize'>,
  request: { indexToken?: string; offset: number; pageSize: number },
  label: string,
  compareToken = false,
): void {
  if (page.offset !== request.offset || page.pageSize !== request.pageSize) {
    throw new TypeError(`Desktop Host 返回的 ${label} 分页位置与请求不一致。`);
  }
  if (compareToken && page.indexToken !== request.indexToken) {
    throw new TypeError(`Desktop Host 返回的 ${label} 索引令牌与请求不一致。`);
  }
}

function validateTargets(value: unknown): SelectionTarget[] {
  if (!Array.isArray(value) || value.length === 0 || value.length > 1_000) throw new TypeError('目标列表必须包含 1–1000 项。');
  let totalPageIndexes = 0;
  return value.map((item, index) => {
    if (!isRecord(item)) throw new TypeError(`目标 ${index + 1} 无效。`);
    const avid = assertString(item.avid, 'avid', 64);
    let pageIndexes: number[] | undefined;
    if (item.pageIndexes !== undefined) {
      if (!Array.isArray(item.pageIndexes) || item.pageIndexes.length > 10_000) throw new TypeError('页面索引列表无效。');
      totalPageIndexes += item.pageIndexes.length;
      if (totalPageIndexes > 20_000) throw new TypeError('单次操作的页面索引总数不得超过 20000。');
      pageIndexes = item.pageIndexes.map((page) => assertInteger(page, 'pageIndex', 0, 1_000_000));
    }
    return { avid, pageIndexes };
  });
}

function validateStringArray(value: unknown, name: string, maximumCount = 1_000): string[] {
  if (!Array.isArray(value) || value.length > maximumCount) {
    throw new TypeError(`${name} 必须是最多包含 ${maximumCount} 项的数组。`);
  }
  return value.map((item) => assertString(item, name, 1_024));
}

function validateNonEmptyStringArray(value: unknown, name: string, maximumCount = 1_000): string[] {
  const result = validateStringArray(value, name, maximumCount);
  if (result.length === 0) throw new TypeError(name + ' 必须至少包含 1 项。');
  return result;
}

function validatePlayer(value: unknown): PlayerPreference {
  if (value !== 'system' && value !== 'mpv' && value !== 'vlc') throw new TypeError('无效播放器偏好。');
  return value;
}

function optionalPath(value: unknown): string | undefined {
  return value === undefined || value === '' ? undefined : assertPath(value, 'defaultPath');
}

function assertPath(value: unknown, name: string): string {
  const result = assertString(value, name, 32_768).trim();
  if (!result || result.includes('\0')) throw new TypeError(`${name} 不是有效路径。`);
  return result;
}

function assertString(value: unknown, name: string, max: number): string {
  if (typeof value !== 'string' || value.length > max) throw new TypeError(`${name} 必须是长度不超过 ${max} 的字符串。`);
  return value;
}

function assertNonEmptyString(value: unknown, name: string, max: number): string {
  const result = assertString(value, name, max).trim();
  if (!result) throw new TypeError(`${name} 不能为空。`);
  return result;
}

function assertBoolean(value: unknown, name: string): boolean {
  if (typeof value !== 'boolean') throw new TypeError(`${name} 必须是布尔值。`);
  return value;
}

function assertInteger(value: unknown, name: string, min: number, max: number): number {
  if (!Number.isInteger(value) || (value as number) < min || (value as number) > max) throw new TypeError(`${name} 必须在 ${min}–${max} 之间。`);
  return value as number;
}

function safeFileName(value: string): string {
  const name = path.basename(value).replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').trim();
  return name || 'export';
}
