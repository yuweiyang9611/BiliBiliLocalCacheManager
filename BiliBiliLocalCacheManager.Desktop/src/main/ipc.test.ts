import { EventEmitter } from 'node:events';
import path from 'node:path';
import type { IpcMainInvokeEvent } from 'electron';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { JsonObject } from '../shared/contracts';
import { channels } from '../shared/channels';
import type { DesktopHostBridge } from './host-bridge';

const electronMocks = vi.hoisted(() => ({
  handlers: new Map<string, (...args: unknown[]) => unknown>(),
  handle: vi.fn((channel: string, listener: (...args: unknown[]) => unknown) => {
    electronMocks.handlers.set(channel, listener);
  }),
  removeHandler: vi.fn((channel: string) => {
    electronMocks.handlers.delete(channel);
  }),
  fromWebContents: vi.fn(() => null),
  getPath: vi.fn(() => path.resolve('downloads')),
  showOpenDialog: vi.fn(),
  showSaveDialog: vi.fn(),
  showMessageBox: vi.fn(),
  openPath: vi.fn(),
}));

vi.mock('electron', () => ({
  BrowserWindow: { fromWebContents: electronMocks.fromWebContents },
  app: {
    getPath: electronMocks.getPath,
    getVersion: () => '0.4.0',
  },
  dialog: {
    showOpenDialog: electronMocks.showOpenDialog,
    showSaveDialog: electronMocks.showSaveDialog,
    showMessageBox: electronMocks.showMessageBox,
  },
  ipcMain: {
    handle: electronMocks.handle,
    removeHandler: electronMocks.removeHandler,
  },
  shell: { openPath: electronMocks.openPath },
}));

import { registerIpc } from './ipc';

interface Deferred {
  promise: Promise<unknown>;
  resolve(value: unknown): void;
  reject(error: unknown): void;
}

let unregister: (() => void) | undefined;

beforeEach(() => {
  electronMocks.handlers.clear();
  vi.clearAllMocks();
  electronMocks.getPath.mockReturnValue(path.resolve('downloads'));
});

afterEach(() => {
  unregister?.();
  unregister = undefined;
});

describe('IPC cancellable request tracking', () => {
  it('cancels every concurrent request from the renderer, including search', async () => {
    const fake = createDeferredBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(7);

    const scan = invoke(channels.scan, event, { rootPath: path.resolve('cache'), includeIncomplete: false });
    const search = invoke(channels.search, event, validSearchRequest());
    const requestIds = fake.calls
      .filter((call) => call.method === 'scan' || call.method === 'search')
      .map((call) => call.id);

    expect(await invoke(channels.cancel, event)).toBe(true);
    expect(fake.cancelledIds).toEqual(requestIds);

    const results = await Promise.allSettled([scan, search]);
    expect(results.every((result) => result.status === 'rejected')).toBe(true);
  });

  it('does not let an earlier completion remove a later active request', async () => {
    const fake = createDeferredBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(11);

    const scan = invoke(channels.scan, event, { rootPath: path.resolve('cache'), includeIncomplete: false });
    const scanId = fake.calls.find((call) => call.method === 'scan')!.id;
    const search = invoke(channels.search, event, validSearchRequest());
    const searchId = fake.calls.find((call) => call.method === 'search')!.id;

    fake.pending.get(scanId)!.resolve(validScanResult());
    await scan;
    expect(await invoke(channels.cancel, event)).toBe(true);
    expect(fake.cancelledIds).toEqual([searchId]);

    await expect(search).rejects.toThrow('操作已取消');
    expect(await invoke(channels.cancel, event)).toBe(false);
  });

  it('cancels every tracked long-running Host call when webContents is destroyed', async () => {
    const fake = createDeferredBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(13);
    const cacheRoot = path.resolve('cache');
    electronMocks.showMessageBox.mockResolvedValue({ response: 0 });
    electronMocks.showSaveDialog.mockResolvedValue({ canceled: false, filePath: path.resolve('exports', 'result.bin') });

    const operations = [
      invoke(channels.storageGet, event, cacheRoot),
      invoke(channels.artifactsCleanup, event),
      invoke(channels.artifactsClear, event),
      invoke(channels.artifactsOpen, event),
      invoke(channels.trashMove, event, cacheRoot, ['100']),
      invoke(channels.trashList, event, cacheRoot),
      invoke(channels.trashRestore, event, cacheRoot, ['trash-100']),
      invoke(channels.trashPurge, event, cacheRoot, ['trash-100']),
      invoke(channels.play, event, cacheRoot, [{ avid: '100' }], 'system', false),
      invoke(channels.exportMedia, event, cacheRoot, [{ avid: '100', pageIndexes: [1] }], 'result.mp4', false),
      invoke(channels.exportDiagnostics, event, 'diagnostics.zip', cacheRoot),
    ];
    const settled = Promise.allSettled(operations);
    await vi.waitFor(() => expect(fake.calls.map((call) => call.method)).toEqual(expect.arrayContaining([
      'storage.get',
      'artifacts.cleanup',
      'artifacts.clear',
      'trash.move',
      'trash.list',
      'trash.restore',
      'trash.purge',
      'play',
      'export',
      'diagnostics.export',
    ])));
    const activeIds = fake.calls.map((call) => call.id);

    event.sender.emit('destroyed');

    expect(fake.cancelledIds).toEqual(activeIds);
    expect((await settled).every((result) => result.status === 'rejected')).toBe(true);
  });

  it('cancels only active cache.details work through the scoped channel', async () => {
    const fake = createDeferredBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(14);
    const details = invoke(channels.cacheDetails, event, {
      indexToken: 'index-token-1',
      avid: '100',
      offset: 0,
      pageSize: 100,
    });
    const storage = invoke(channels.storageGet, event, path.resolve('cache'));
    const detailsId = fake.calls.find((call) => call.method === 'cache.details')!.id;

    expect(await invoke(channels.cacheDetailsCancel, event)).toBe(true);
    expect(fake.cancelledIds).toEqual([detailsId]);
    await expect(details).rejects.toThrow('操作已取消');
    expect(await invoke(channels.cancel, event)).toBe(true);
    await expect(storage).rejects.toThrow('操作已取消');
  });
});

describe('IPC Host contract wiring', () => {
  it('rejects a malformed initialState response from the Host', async () => {
    const fake = createImmediateBridge({
      initialState: { protocolVersion: 2 },
    });
    unregister = registerIpc(fake.bridge, () => null);

    await expect(invoke(channels.initialState, trustedEvent(15))).rejects.toThrow(
      /initialState\.settings 必须是对象/,
    );
    expect(fake.calls).toEqual([expect.objectContaining({ method: 'initialState' })]);
  });

  it('forwards scan pagination and maps a validated page response', async () => {
    const response = validScanResult({ offset: 40, pageSize: 20, totalItems: 41 });
    const fake = createImmediateBridge({ scan: response });
    unregister = registerIpc(fake.bridge, () => null);
    const rootPath = path.resolve('paged-cache');

    const result = await invoke(channels.scan, trustedEvent(16), {
      rootPath,
      includeIncomplete: true,
      persistSettings: false,
      offset: 40,
      pageSize: 20,
    });

    expect(fake.calls.find((call) => call.method === 'scan')?.params).toEqual({
      rootPath,
      includeIncomplete: true,
      persistSettings: false,
      offset: 40,
      pageSize: 20,
    });
    expect(result).toEqual(response);
  });

  it('rejects Host pages that do not match the request token or offset', async () => {
    const fake = createImmediateBridge({
      search: validScanResult({ indexToken: 'different-token' }),
      scan: validScanResult({ offset: 1, items: [] }),
    });
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(17);

    await expect(invoke(channels.search, event, validSearchRequest())).rejects.toThrow(/索引令牌与请求不一致/);
    await expect(invoke(channels.scan, event, {
      rootPath: path.resolve('cache'),
      includeIncomplete: false,
    })).rejects.toThrow(/分页位置与请求不一致/);
  });

  it('applies cache.details request defaults and maps the validated segment page', async () => {
    const response = validCacheDetails();
    const fake = createImmediateBridge({ 'cache.details': response });
    unregister = registerIpc(fake.bridge, () => null);

    const result = await invoke(channels.cacheDetails, trustedEvent(18), {
      indexToken: 'index-token-1',
      avid: '100',
    });

    expect(fake.calls.find((call) => call.method === 'cache.details')?.params).toEqual({
      indexToken: 'index-token-1',
      avid: '100',
      offset: 0,
      pageSize: 100,
    });
    expect(result).toEqual(response);
  });
});

describe('IPC export destinations', () => {
  it('uses a save-file dialog only for one explicitly selected page', async () => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(17);
    const selectedPath = path.resolve('exports', 'one.mp4');
    electronMocks.showSaveDialog.mockResolvedValue({ canceled: false, filePath: selectedPath });

    await invoke(
      channels.exportMedia,
      event,
      path.resolve('cache'),
      [{ avid: '123', pageIndexes: [1] }],
      '../../renderer-controlled.mp4',
      false,
    );

    expect(electronMocks.showSaveDialog).toHaveBeenCalledWith(expect.objectContaining({
      title: '导出 MP4',
      defaultPath: path.join(path.resolve('downloads'), 'renderer-controlled.mp4'),
    }));
    expect(electronMocks.showOpenDialog).not.toHaveBeenCalled();
    expect(fake.calls.find((call) => call.method === 'export')?.params.outputPath).toBe(selectedPath);
  });

  it.each([
    ['multiple pages', [{ avid: '123', pageIndexes: [1, 2] }]],
    ['multiple caches', [{ avid: '123' }, { avid: '456' }]],
    ['a whole cache whose page count is resolved by the Host', [{ avid: '123' }]],
  ])('uses a directory dialog for %s', async (_label, targets) => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(23);
    const selectedDirectory = path.resolve('exports', 'batch');
    electronMocks.showOpenDialog.mockResolvedValue({
      canceled: false,
      filePaths: [selectedDirectory],
    });

    await invoke(channels.exportMedia, event, path.resolve('cache'), targets, 'batch.mp4', false);

    expect(electronMocks.showOpenDialog).toHaveBeenCalledWith(expect.objectContaining({
      title: '选择 MP4 导出目录',
      properties: ['openDirectory', 'createDirectory'],
    }));
    expect(electronMocks.showSaveDialog).not.toHaveBeenCalled();
    expect(fake.calls.find((call) => call.method === 'export')?.params.outputPath)
      .toBe(selectedDirectory);
  });
});

describe('IPC trash root safety', () => {
  it('requires an explicit root and a non-empty entry snapshot before showing confirmation', async () => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(29);

    await expect(invoke(channels.trashPurge, event, '', ['entry-1'])).rejects.toThrow('rootPath');
    await expect(invoke(channels.trashPurge, event, path.resolve('cache'), [])).rejects.toThrow('至少包含 1 项');

    expect(electronMocks.showMessageBox).not.toHaveBeenCalled();
    expect(fake.calls.some((call) => call.method === 'trash.purge')).toBe(false);
  });

  it('binds permanent purge to the displayed root and complete entry id snapshot', async () => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(31);
    const rootPath = path.resolve('cache-b');
    electronMocks.showMessageBox.mockResolvedValue({ response: 0 });

    await invoke(channels.trashPurge, event, rootPath, ['entry-1', 'entry-2']);

    expect(electronMocks.showMessageBox).toHaveBeenCalledWith(expect.objectContaining({
      detail: expect.stringContaining(rootPath),
    }));
    expect(fake.calls.find((call) => call.method === 'trash.purge')?.params).toEqual({
      rootPath,
      entryIds: ['entry-1', 'entry-2'],
      confirmed: true,
    });
  });

  it('accepts a complete purge snapshot larger than the generic batch limit', async () => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(33);
    const rootPath = path.resolve('large-trash');
    const entryIds = Array.from({ length: 1_001 }, (_, index) => `entry-${index}`);
    electronMocks.showMessageBox.mockResolvedValue({ response: 0 });

    await invoke(channels.trashPurge, event, rootPath, entryIds);

    expect(fake.calls.find((call) => call.method === 'trash.purge')?.params).toEqual({
      rootPath,
      entryIds,
      confirmed: true,
    });
  });
});

function createDeferredBridge(): {
  bridge: DesktopHostBridge;
  calls: Array<{ id: string; method: string; params: JsonObject }>;
  pending: Map<string, Deferred>;
  cancelledIds: string[];
} {
  const calls: Array<{ id: string; method: string; params: JsonObject }> = [];
  const pending = new Map<string, Deferred>();
  const cancelledIds: string[] = [];
  let sequence = 0;
  const emitter = new EventEmitter() as EventEmitter & {
    call<T>(method: string, params?: JsonObject): { id: string; promise: Promise<T>; cancel(): boolean };
  };
  emitter.call = <T>(method: string, params: JsonObject = {}) => {
    const id = `${method}-${++sequence}`;
    calls.push({ id, method, params });
    const operation = deferred();
    pending.set(id, operation);
    let active = true;
    operation.promise.then(
      () => { active = false; },
      () => { active = false; },
    );
    return {
      id,
      promise: operation.promise as Promise<T>,
      cancel: () => {
        if (!active) return false;
        active = false;
        cancelledIds.push(id);
        operation.reject(new Error('操作已取消。'));
        return true;
      },
    };
  };
  return { bridge: emitter as unknown as DesktopHostBridge, calls, pending, cancelledIds };
}

function createImmediateBridge(results: Record<string, unknown> = {}): {
  bridge: DesktopHostBridge;
  calls: Array<{ id: string; method: string; params: JsonObject }>;
} {
  const calls: Array<{ id: string; method: string; params: JsonObject }> = [];
  let sequence = 0;
  const emitter = new EventEmitter() as EventEmitter & {
    call<T>(method: string, params?: JsonObject): { id: string; promise: Promise<T>; cancel(): boolean };
  };
  emitter.call = <T>(method: string, params: JsonObject = {}) => {
    const id = `${method}-${++sequence}`;
    calls.push({ id, method, params });
    return {
      id,
      promise: Promise.resolve((Object.hasOwn(results, method)
        ? results[method]
        : { outputPath: params.outputPath }) as T),
      cancel: () => false,
    };
  };
  return { bridge: emitter as unknown as DesktopHostBridge, calls };
}

function deferred(): Deferred {
  let resolve!: (value: unknown) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<unknown>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function trustedEvent(senderId: number): IpcMainInvokeEvent {
  const frame = { url: 'blcm://app/index.html', top: null as unknown };
  frame.top = frame;
  const sender = Object.assign(new EventEmitter(), {
    id: senderId,
    isDestroyed: () => false,
  });
  return {
    sender,
    senderFrame: frame,
  } as unknown as IpcMainInvokeEvent;
}

function invoke(channel: string, event: IpcMainInvokeEvent, ...args: unknown[]): Promise<unknown> {
  const handler = electronMocks.handlers.get(channel);
  if (!handler) throw new Error(`Missing IPC handler for ${channel}`);
  return Promise.resolve(handler(event, ...args));
}

function validSearchRequest(): JsonObject {
  return {
    indexToken: 'index-token-1',
    offset: 0,
    pageSize: 100,
    keyword: 'demo',
    matchMode: 'contains',
    splitKeywords: true,
    anyKeywords: false,
    includePartName: true,
    includeOwnerName: true,
    includeBvid: true,
    includeAvid: true,
    caseSensitive: false,
  };
}

function validCacheEntry(): JsonObject {
  return {
    id: '100',
    avid: '100',
    bvid: 'BV1demo',
    title: '测试缓存',
    ownerName: '测试 UP',
    durationSeconds: 125,
    segmentCount: 1,
    sizeBytes: 32 * 1024 * 1024,
    isAllCompleted: true,
    lastUpdated: '2026-08-26T00:00:00Z',
  };
}

function validScanResult(overrides: JsonObject = {}): JsonObject {
  const result: JsonObject = {
    rootPath: path.resolve('paged-cache'),
    indexToken: 'index-token-1',
    offset: 0,
    pageSize: 100,
    totalItems: 1,
    hasMore: false,
    items: [validCacheEntry()],
    includedEntries: 1,
    skippedIncompleteEntries: 2,
    invalidEntries: 1,
    inaccessibleDirectories: 0,
  };
  return { ...result, ...overrides };
}

function validCacheDetails(): JsonObject {
  return {
    indexToken: 'index-token-1',
    avid: '100',
    item: validCacheEntry(),
    offset: 0,
    pageSize: 100,
    totalItems: 1,
    hasMore: false,
    segments: [{
      id: '100:1',
      segmentKey: '1',
      pageIndex: 1,
      partName: '第一集',
      structureKind: 'Dash',
      materialKind: 'AudioVideo',
      sizeBytes: 32 * 1024 * 1024,
      durationSeconds: 125,
      isPlayable: true,
      directoryPath: path.resolve('cache', '100', '1'),
    }],
  };
}
