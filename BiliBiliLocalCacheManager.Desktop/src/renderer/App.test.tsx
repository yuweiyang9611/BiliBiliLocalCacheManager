// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  CacheDetails,
  CacheEntry,
  CacheManagerApi,
  CachePage,
  CacheSegment,
  InitialState,
  ScanResult,
} from '../shared/contracts';
import { defaultSettings, emptyStorage } from '../shared/contracts';
import { App } from './App';
import { IpcError } from '../shared/ipc-result';

const indexToken = 'index-token';
const initialSegments: CacheSegment[] = [{
  id: '100:1',
  segmentKey: '1',
  pageIndex: 1,
  partName: '第一集',
  structureKind: 'Dash',
  materialKind: 'AudioVideo',
  sizeBytes: 32 * 1024 * 1024,
  durationSeconds: 125,
  isPlayable: true,
}];
const initialItem: CacheEntry = {
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
const initial: InitialState = {
  protocolVersion: 3,
  settings: { ...defaultSettings, rootPath: 'D:\\Bilibili\\download', scanOnStartup: false },
  settingsState: { canSave: true, sourceSchemaVersion: 2 },
  items: [initialItem],
  storage: emptyStorage,
  trash: [],
  capabilities: { playback: true, exportMedia: true, cacheDetails: true, trashPurge: false, nativeWayland: false },
};

function createCachePage(
  items: CacheEntry[] = initial.items,
  overrides: Partial<CachePage> = {},
): ScanResult {
  return {
    indexToken,
    issues: [],
    issuesTruncated: false,
    offset: 0,
    pageSize: 100,
    totalItems: items.length,
    hasMore: false,
    items,
    ...overrides,
  };
}

function createCacheDetails(
  segments: CacheSegment[] = initialSegments,
  overrides: Partial<CacheDetails> = {},
): CacheDetails {
  return {
    indexToken,
    avid: initialItem.avid,
    item: initialItem,
    offset: 0,
    pageSize: 100,
    totalItems: segments.length,
    hasMore: false,
    segments,
    ...overrides,
  };
}

function createApi(): CacheManagerApi {
  const api: CacheManagerApi = {
    getTrashPage: vi.fn().mockImplementation(async (rootPath, options) => {
      const items = await api.listTrash(rootPath);
      const offset = options?.offset ?? 0;
      const pageSize = options?.pageSize ?? 100;
      return { snapshotToken: 'trash-snapshot', offset, pageSize, totalItems: items.length, totalSizeBytes: items.reduce((n, x) => n + x.sizeBytes, 0), hasMore: offset + pageSize < items.length, items: items.slice(offset, offset + pageSize) };
    }),
    purgeTrashSnapshot: vi.fn().mockImplementation(async (rootPath, _snapshotToken, confirmationText) => api.purgeTrash(rootPath, (await api.listTrash(rootPath)).map(x => x.id), confirmationText)),
    health: vi.fn().mockResolvedValue({ protocolVersion: 3, status: 'ok', version: '1.0.0' }),
    getInitialState: vi.fn().mockResolvedValue(initial),
    updateSettings: vi.fn().mockImplementation(async (patch) => ({ ...initial.settings, ...patch })),
    chooseRootDirectory: vi.fn().mockResolvedValue(null),
    scan: vi.fn().mockResolvedValue(createCachePage()),
    locateScanIssue: vi.fn().mockResolvedValue(true),
    cancel: vi.fn().mockResolvedValue(true),
    cancelSearch: vi.fn().mockResolvedValue(true),
    acknowledgeUncertain: vi.fn().mockResolvedValue(true),
    onOperationState: vi.fn().mockReturnValue(() => undefined),
    getOperationStates: vi.fn().mockResolvedValue([]),
    search: vi.fn().mockResolvedValue(createCachePage()),
    getCacheDetails: vi.fn().mockResolvedValue(createCacheDetails()),
    cancelCacheDetails: vi.fn().mockResolvedValue(false),
    getStorage: vi.fn().mockResolvedValue(emptyStorage),
    cleanupTranscodeCache: vi.fn().mockResolvedValue({ deletedFileCount: 1, freedBytes: 1024, failedFileCount: 0, remainingBytes: 0 }),
    clearTranscodeCache: vi.fn().mockResolvedValue({ deletedFileCount: 1, freedBytes: 1024, failedFileCount: 0, remainingBytes: 0 }),
    openTranscodeCache: vi.fn().mockResolvedValue(true),
    moveToTrash: vi.fn().mockResolvedValue({ moved: [], failed: [] }),
    listTrash: vi.fn().mockResolvedValue([]),
    restoreTrash: vi.fn().mockResolvedValue({ restored: [], failed: [] }),
    purgeTrash: vi.fn().mockResolvedValue({ purged: [], failed: [] }),
    play: vi.fn().mockResolvedValue({ queued: 1, failures: [] }),
    exportMedia: vi.fn().mockResolvedValue(null),
    exportDiagnostics: vi.fn().mockResolvedValue(null),
    getDesktopInfo: vi.fn().mockResolvedValue({
      appVersion: '0.4.0',
      electronVersion: '44.0.0',
      chromiumVersion: '140.0.0',
      nodeVersion: '24.0.0',
      platform: 'linux',
      arch: 'x64',
      displayBackend: 'x11',
    }),
    onProgress: vi.fn().mockReturnValue(() => undefined),
    onHostUnavailable: vi.fn().mockReturnValue(() => undefined),
  };
  return api;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((complete, fail) => {
    resolve = complete;
    reject = fail;
  });
  return { promise, resolve, reject };
}

describe('desktop renderer', () => {
  it('keeps bootstrap usable when advisory health times out', async () => {
    vi.mocked(api.health).mockRejectedValue(new IpcError('slow health', 'HOST_TIMEOUT'));
    const { container } = render(<App />);
    await waitFor(() => expect(container.querySelector('[data-renderer-bootstrap]')).toHaveAttribute('data-renderer-bootstrap', 'ready'));
    expect(screen.queryByText('桌面端初始化失败')).not.toBeInTheDocument();
    expect(screen.getByText('运行环境检查暂不可用：slow health')).toBeInTheDocument();
  });

  it('does not wait for a pending health reply before allowing scans', async () => {
    const health = deferred<Awaited<ReturnType<CacheManagerApi['health']>>>();
    vi.mocked(api.health).mockReturnValue(health.promise);
    const { container } = render(<App />);
    await waitFor(() => expect(container.querySelector('[data-renderer-bootstrap]')).toHaveAttribute('data-renderer-bootstrap', 'ready'));
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    await act(async () => health.resolve({ protocolVersion: 3, status: 'ok', version: 'test' }));
  });

  it('pages trash by snapshot and confirms the full snapshot from a later page', async () => {
    const entries = Array.from({ length: 205 }, (_, n) => ({ id: `trash-${n}`, avid: String(n + 1), title: `Trash ${n}`, sizeBytes: 1, deletedAt: null }));
    vi.mocked(api.listTrash).mockResolvedValue(entries);
    vi.mocked(api.getInitialState).mockResolvedValue({ ...initial, capabilities: { ...initial.capabilities, trashPurge: true } });
    vi.mocked(api.purgeTrashSnapshot).mockResolvedValue(null);
    render(<App />);
    await screen.findByText('服务正常');
    fireEvent.click(screen.getByRole('button', { name: '回收站' }));
    await screen.findByText('Trash 0');
    expect(screen.queryByText('Trash 100')).not.toBeInTheDocument();
    fireEvent.click(within(screen.getByLabelText('回收站分页')).getByRole('button', { name: '下一页' }));
    await screen.findByText('Trash 100');
    expect(api.getTrashPage).toHaveBeenLastCalledWith(initial.settings.rootPath, { offset: 100, pageSize: 100, snapshotToken: 'trash-snapshot' });
    fireEvent.click(screen.getByRole('button', { name: '清空回收站' }));
    expect(screen.getByText('彻底清空回收站（205 项）')).toBeInTheDocument();
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: '确认' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText('输入确认文字'), { target: { value: '永久删除' } });
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '确认' }));
    await waitFor(() => expect(api.purgeTrashSnapshot).toHaveBeenCalledWith(initial.settings.rootPath, 'trash-snapshot', '永久删除'));
    expect(api.purgeTrash).not.toHaveBeenCalled();
  });
  let api: CacheManagerApi;
  let hostUnavailableListener: ((message: string) => void) | null;

  beforeEach(() => {
    api = createApi();
    hostUnavailableListener = null;
    vi.mocked(api.onHostUnavailable).mockImplementation((listener) => {
      hostUnavailableListener = listener;
      return () => undefined;
    });
    Object.defineProperty(window, 'cacheManager', { configurable: true, value: api });
  });

  afterEach(cleanup);

  it('keeps selected videos when paging and submits the complete selection', async () => {
    const second = { ...initialItem, id: '200', avid: '200', title: 'Second page video' };
    vi.mocked(api.scan).mockResolvedValue(createCachePage([initialItem], { totalItems: 101, hasMore: true }));
    vi.mocked(api.search).mockImplementation(async request => createCachePage(
      request.offset ? [second] : [initialItem], { offset: request.offset, totalItems: 101, hasMore: !request.offset }));
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(within(screen.getByLabelText('缓存分页')).getByRole('button', { name: '下一页' }));
    await screen.findByText('Second page video');
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 Second page video' }));
    fireEvent.click(screen.getByRole('button', { name: '查看已选清单' }));
    expect(within(screen.getByRole('dialog')).getByText('测试缓存')).toBeInTheDocument();
    expect(within(screen.getByRole('dialog')).getByText('Second page video')).toBeInTheDocument();
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '关闭' }));
    fireEvent.click(screen.getByRole('button', { name: '播放' }));
    await waitFor(() => expect(api.play).toHaveBeenCalledWith(initial.settings.rootPath,
      [{ avid: '100' }, { avid: '200' }], 'system', false, indexToken));
  });

  it('selects logical parts across videos without losing them when inspecting another video', async () => {
    const second = { ...initialItem, id: '200', avid: '200', title: 'Second video' };
    vi.mocked(api.scan).mockResolvedValue(createCachePage([initialItem, second]));
    vi.mocked(api.getCacheDetails).mockImplementation(async request => request.avid === '100'
      ? createCacheDetails([{ ...initialSegments[0], id: '100:source-a' }])
      : createCacheDetails([{ ...initialSegments[0], id: '200:source-b', pageIndex: 3, partName: 'Third part' }], { avid: '200', item: second }));
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await screen.findByText('Second video');
    fireEvent.click(screen.getByText('测试缓存'));
    fireEvent.click(await screen.findByRole('checkbox', { name: '选择分段 第一集' }));
    fireEvent.click(screen.getByText('Second video'));
    fireEvent.click(await screen.findByRole('checkbox', { name: '选择分段 Third part' }));
    fireEvent.click(screen.getByRole('button', { name: '播放' }));
    await waitFor(() => expect(api.play).toHaveBeenCalledWith(initial.settings.rootPath,
      [{ avid: '100', pageIndexes: [1] }, { avid: '200', pageIndexes: [3] }], 'system', false, indexToken));
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 Second video' }));
    expect(screen.getByRole('checkbox', { name: '选择分段 Third part' })).not.toBeChecked();
  });

  it('searches during export without changing the submitted targets', async () => {
    const exporting = deferred<Awaited<ReturnType<CacheManagerApi['exportMedia']>>>();
    vi.mocked(api.exportMedia).mockReturnValue(exporting.promise);
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '导出' }));
    await waitFor(() => expect(api.exportMedia).toHaveBeenCalledOnce());
    fireEvent.change(screen.getByPlaceholderText('搜索标题、UP 主、BV 号或 AV 号'), { target: { value: 'other' } });
    await waitFor(() => expect(api.search).toHaveBeenCalledWith(expect.objectContaining({ keyword: 'other' })));
    expect(screen.getByRole('button', { name: '浏览缓存目录' })).toBeDisabled();
    expect(vi.mocked(api.exportMedia).mock.calls[0][1]).toEqual([{ avid: '100' }]);
    await act(async () => exporting.resolve({ published: true, outputPath: 'batch', exportedCount: 1, failures: [] }));
  });

  it('clears old-page selections made while a different query is still pending', async () => {
    const pending = deferred<CachePage>();
    vi.mocked(api.search).mockReturnValue(pending.promise);
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    fireEvent.change(screen.getByPlaceholderText('搜索标题、UP 主、BV 号或 AV 号'), { target: { value: 'new' } });
    await waitFor(() => expect(api.search).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    await act(async () => pending.resolve(createCachePage([{ ...initialItem, id: '200', avid: '200', title: 'New result' }])));
    expect(screen.getByRole('button', { name: '播放' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: '查看已选清单' })).not.toBeInTheDocument();
  });

  it('requires explicit transcode consent and retries the original export snapshot', async () => {
    const approvalToken = 'a'.repeat(64);
    vi.mocked(api.exportMedia).mockResolvedValueOnce({ published: false, outputPath: null, exportedCount: 0, failures: [],
      confirmationId: 'confirmation-1', transcodeRequirements: [{ avid: '100', pageIndex: 1, title: '测试缓存',
        approvalToken, processingKind: 'audio-aac', reason: 'Unsupported audio', impact: 'Audio will be re-encoded' }] })
      .mockResolvedValueOnce({ published: true, outputPath: 'batch', exportedCount: 1, failures: [] });
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '导出' }));
    expect(await screen.findByText('导出结果：待确认转码')).toBeInTheDocument();
    expect(api.exportMedia).toHaveBeenCalledOnce();
    expect(screen.getByText(/Audio will be re-encoded/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '清空选择' }));
    fireEvent.click(screen.getByRole('button', { name: '确认转码并重试整批' }));
    await waitFor(() => expect(api.exportMedia).toHaveBeenCalledTimes(2));
    expect(vi.mocked(api.exportMedia).mock.calls[1][1]).toEqual([{ avid: '100' }]);
    expect(vi.mocked(api.exportMedia).mock.calls[1][4]).toEqual({ id: 'confirmation-1', approvals: [approvalToken] });
  });

  it('deletes only selected parts and undoes the exact returned trash identities', async () => {
    vi.mocked(api.moveToTrash).mockResolvedValue({ moved: ['100'], failed: [], entryIds: ['this-part-entry'] });
    vi.mocked(api.restoreTrash).mockResolvedValue({ restored: ['this-part-entry'], failed: [] });
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByText('测试缓存'));
    fireEvent.click(await screen.findByRole('checkbox', { name: '选择分段 第一集' }));
    fireEvent.click(screen.getByRole('button', { name: '删除' }));
    expect(screen.getByRole('dialog')).toHaveTextContent('1 个视频中的 1 个分 P');
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '确认' }));
    await waitFor(() => expect(api.moveToTrash).toHaveBeenCalledWith(initial.settings.rootPath, indexToken,
      [{ avid: '100', pageIndexes: [1] }]));
    await waitFor(() => expect(screen.getByRole('button', { name: /撤销删除/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /撤销删除/ }));
    await waitFor(() => expect(api.restoreTrash).toHaveBeenCalledWith(initial.settings.rootPath, ['this-part-entry']));
    expect(api.getTrashPage).not.toHaveBeenCalled();
  });

  it('shows damaged scan entries and locates an issue using its index token', async () => {
    vi.mocked(api.scan).mockResolvedValue({ ...createCachePage(), invalidEntries: 2, hasWarnings: true,
      issues: [{ id: 0, kind: 'InvalidEntry', path: 'damaged/entry.json', message: 'Invalid JSON' }], issuesTruncated: true });
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    expect(await screen.findByText('Invalid JSON')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '定位问题 1', hidden: true }));
    await waitFor(() => expect(api.locateScanIssue).toHaveBeenCalledWith(indexToken, 0));
  });

  it('shows all-failed playback as failure and retries only the failed page', async () => {
    vi.mocked(api.play).mockResolvedValueOnce({ queued: 0, failures: [{ avid: '100', pageIndex: 1, title: 'Failed page', message: 'Missing media' }] })
      .mockResolvedValueOnce({ queued: 1, failures: [] });
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: '播放' })).toBeDisabled());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '播放' }));
    expect(await screen.findByText('播放结果：失败')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '重试未成功项目' }));
    await waitFor(() => expect(api.play).toHaveBeenLastCalledWith(initial.settings.rootPath, [{ avid: '100', pageIndexes: [1] }], 'system', false));
    expect(await screen.findByText('播放结果：全部成功')).toBeInTheDocument();
  });

  it('keeps failed exports unpublished and retries the original batch', async () => {
    vi.mocked(api.exportMedia).mockResolvedValue({ published: false, outputPath: null, exportedCount: 0,
      failures: [{ avid: '100', pageIndex: 1, title: 'Failed page', message: 'Missing media' }] });
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '导出' }));
    expect(await screen.findByText('导出结果：失败')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '重试完整批次' }));
    await waitFor(() => expect(api.exportMedia).toHaveBeenCalledTimes(2));
    expect(vi.mocked(api.exportMedia).mock.calls[1][1]).toEqual([{ avid: '100' }]);
  });

  it('groups more than 1000 failed playback pages into one bounded retry request', async () => {
    const pages = Array.from({ length: 1001 }, (_, index) => index + 2);
    vi.mocked(api.play).mockResolvedValueOnce({ queued: 1, failures: pages.map(pageIndex => ({
      avid: '100', pageIndex, title: 'Failed page', message: 'Missing media',
    })) }).mockResolvedValueOnce({ queued: pages.length, failures: [] });
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '播放' }));
    expect(await screen.findByText('播放结果：部分失败')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '重试未成功项目' }));
    await waitFor(() => expect(api.play).toHaveBeenLastCalledWith(initial.settings.rootPath,
      [{ avid: '100', pageIndexes: pages }], 'system', false));
    expect(api.play).toHaveBeenCalledTimes(2);
    expect(await screen.findByText('播放结果：全部成功')).toBeInTheDocument();
  });

  it('shows pending cancellation and then the actual successful export result', async () => {
    const pending = deferred<Awaited<ReturnType<CacheManagerApi['exportMedia']>>>();
    vi.mocked(api.exportMedia).mockReturnValue(pending.promise);
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '导出' }));
    const state = vi.mocked(api.onOperationState).mock.calls[0][0];
    await act(async () => state({ requestId: 'export-1', operation: 'export', state: 'cancelling', sideEffects: true }));
    expect(screen.getByRole('button', { name: '正在取消' })).toBeDisabled();
    await act(async () => state({ requestId: 'export-1', operation: 'export', state: 'unconfirmed', sideEffects: true }));
    expect(screen.getByText('结果待确认')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '导出' })).toBeDisabled();
    expect(screen.queryByText('导出结果：已取消')).not.toBeInTheDocument();
    await act(async () => {
      state({ requestId: 'export-1', operation: 'export', state: 'settled', sideEffects: true });
      pending.resolve({ published: true, exportedCount: 1, outputPath: 'result.mp4', failures: [] });
    });
    expect(await screen.findByText('导出结果：全部成功')).toBeInTheDocument();
    expect(screen.queryByText('结果待确认')).not.toBeInTheDocument();
  });

  it('does not claim an unknown export is unpublished and requires acknowledgement before retry', async () => {
    const pending = deferred<Awaited<ReturnType<CacheManagerApi['exportMedia']>>>();
    vi.mocked(api.exportMedia).mockReturnValue(pending.promise);
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '导出' }));
    const state = vi.mocked(api.onOperationState).mock.calls[0][0];
    await act(async () => {
      state({ requestId: 'export-1', operation: 'export', state: 'unknown', sideEffects: true });
      pending.reject(new IpcError('操作结果无法确认', 'OUTCOME_UNKNOWN'));
    });
    expect(await screen.findByText('导出结果：结果无法确认')).toBeInTheDocument();
    expect(screen.queryByText(/本批导出未发布/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '重试完整批次' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: '已核对结果' }));
    await waitFor(() => expect(api.acknowledgeUncertain).toHaveBeenCalledWith('export-1'));
    expect(screen.getByRole('button', { name: '重试完整批次' })).toBeDisabled();
    await act(async () => state({ requestId: 'export-1', operation: 'export', state: 'settled', sideEffects: true }));
    expect(screen.getByRole('button', { name: '重试完整批次' })).not.toBeDisabled();
  });

  it('restores the acknowledgement entry and restrictions after a renderer remount', async () => {
    const unknown = { requestId: 'old-export', operation: 'export', state: 'unknown' as const, sideEffects: true };
    const first = render(<App />);
    await screen.findByText('测试缓存');
    first.unmount();
    vi.mocked(api.getOperationStates).mockResolvedValue([unknown]);
    render(<App />);
    expect(await screen.findByText('结果无法确认')).toBeInTheDocument();
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    expect(screen.getByRole('button', { name: '导出' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: '已核对结果' }));
    await waitFor(() => expect(api.acknowledgeUncertain).toHaveBeenCalledWith('old-export'));
    expect(screen.getByRole('button', { name: '导出' })).toBeDisabled();
    const listener = vi.mocked(api.onOperationState).mock.calls.at(-1)![0];
    await act(async () => listener({ ...unknown, state: 'settled' }));
    expect(screen.queryByText('结果无法确认')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '导出' })).not.toBeDisabled();
  });

  it.each(['settled', 'unconfirmed'] as const)('keeps a live %s event ahead of an older snapshot', async state => {
    const pending = deferred<Awaited<ReturnType<CacheManagerApi['getOperationStates']>>>();
    vi.mocked(api.getOperationStates).mockReturnValue(pending.promise);
    render(<App />);
    const value = { requestId: 'old-play', operation: 'play', sideEffects: true };
    const listener = vi.mocked(api.onOperationState).mock.calls[0][0];
    await act(async () => listener({ ...value, state }));
    await act(async () => pending.resolve([{ ...value, state: 'cancelling' }]));
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    if (state === 'settled') {
      expect(screen.queryByText('结果待确认')).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: '播放' })).not.toBeDisabled();
    } else {
      expect(screen.getByText('结果待确认')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: '播放' })).toBeDisabled();
    }
  });

  it.each(['unknown', 'unconfirmed'] as const)('allows inspection while %s but keeps mutations blocked', async state => {
    const pending = deferred<Awaited<ReturnType<CacheManagerApi['exportMedia']>>>();
    vi.mocked(api.exportMedia).mockReturnValue(pending.promise);
    vi.mocked(api.getInitialState).mockResolvedValue({ ...initial, capabilities: { ...initial.capabilities, trashPurge: true } });
    vi.mocked(api.listTrash).mockResolvedValue([{ id: 'trash-100', avid: '100', title: '待核对缓存', sizeBytes: 1, deletedAt: null }]);
    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '导出' }));
    const listener = vi.mocked(api.onOperationState).mock.calls[0][0];
    await act(async () => {
      listener({ requestId: 'export-inspect', operation: 'export', state, sideEffects: true });
      if (state === 'unknown') pending.reject(new IpcError('结果尚未返回', 'OUTCOME_UNKNOWN'));
    });
    fireEvent.click(screen.getByRole('button', { name: '存储概览' }));
    await waitFor(() => expect(screen.getByRole('button', { name: '刷新统计' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: '刷新统计' }));
    await waitFor(() => expect(api.getStorage).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByRole('button', { name: '打开转码缓存目录' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: '打开转码缓存目录' }));
    await waitFor(() => expect(api.openTranscodeCache).toHaveBeenCalledOnce());
    expect(screen.getByRole('button', { name: '按策略清理' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '清空转码缓存' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: '回收站' }));
    await screen.findByText('待核对缓存');
    await waitFor(() => expect(screen.getByRole('button', { name: '刷新' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: '刷新' }));
    await waitFor(() => expect(api.listTrash).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 待核对缓存' }));
    expect(screen.getByRole('button', { name: '恢复所选' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '清空回收站' })).toBeDisabled();
    expect(api.acknowledgeUncertain).not.toHaveBeenCalled();
    expect(api.cleanupTranscodeCache).not.toHaveBeenCalled();
    if (state === 'unconfirmed') await act(async () => {
      listener({ requestId: 'export-inspect', operation: 'export', state: 'settled', sideEffects: true });
      pending.resolve({ published: true, exportedCount: 1, outputPath: 'result.mp4', failures: [] });
    });
  });

  it('loads settings and cache rows from Desktop Host', async () => {
    render(<App />);
    expect(await screen.findByDisplayValue('D:\\Bilibili\\download')).toBeInTheDocument();
    expect(screen.getByText('测试缓存')).toBeInTheDocument();
    expect(screen.getByText('测试 UP')).toBeInTheDocument();
    await waitFor(() => expect(api.health).toHaveBeenCalledOnce());
  });

  it('runs a scan through the allowlisted API', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledWith({
      rootPath: 'D:\\Bilibili\\download',
      includeIncomplete: false,
      persistSettings: true,
      offset: 0,
      pageSize: 100,
    }));
  });

  it('does not automatically scan a remembered root unless startup scanning is enabled', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    expect(api.scan).not.toHaveBeenCalled();
  });

  it('does not turn a persisted keyword into an implicit startup scan', async () => {
    vi.mocked(api.getInitialState).mockResolvedValue({
      ...initial,
      settings: { ...initial.settings, keyword: '上次搜索' },
      items: [],
    });

    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    await act(async () => { await new Promise((resolve) => window.setTimeout(resolve, 450)); });
    expect(api.scan).not.toHaveBeenCalled();
    expect(api.search).not.toHaveBeenCalled();
  });

  it('automatically scans only when startup scanning is enabled', async () => {
    const pendingStartupScan = deferred<ScanResult>();
    vi.mocked(api.scan).mockImplementationOnce(() => pendingStartupScan.promise);
    vi.mocked(api.getInitialState).mockResolvedValue({
      ...initial,
      settings: { ...initial.settings, scanOnStartup: true },
      items: [],
    });
    const { container } = render(<App />);
    await waitFor(() => expect(api.scan).toHaveBeenCalledWith({
      rootPath: 'D:\\Bilibili\\download',
      includeIncomplete: false,
      persistSettings: false,
      offset: 0,
      pageSize: 100,
    }));
    const shell = container.querySelector('[data-renderer-bootstrap]');
    expect(shell).toHaveAttribute('data-renderer-bootstrap', 'loading');
    expect(shell).toHaveAttribute('data-settings-loaded', 'true');
    expect(shell).toHaveAttribute('data-startup-scan', 'running');
    expect(shell).not.toHaveAttribute('data-renderer-ready', 'true');

    await act(async () => {
      pendingStartupScan.resolve(createCachePage(initial.items, { totalItems: 7 }));
    });
    await waitFor(() => expect(shell).toHaveAttribute('data-renderer-bootstrap', 'ready'));
    expect(shell).toHaveAttribute('data-renderer-ready', 'true');
    expect(shell).toHaveAttribute('data-settings-loaded', 'true');
    expect(shell).toHaveAttribute('data-startup-scan', 'completed');
    expect(shell).toHaveAttribute('data-startup-scan-count', '7');
  });

  it('marks bootstrap as failed when initialState is rejected', async () => {
    vi.mocked(api.getInitialState).mockRejectedValueOnce(
      new Error('Desktop Host initialState 格式无效。'),
    );
    const { container } = render(<App />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Desktop Host initialState 格式无效。',
    );
    const shell = container.querySelector('[data-renderer-bootstrap]');
    expect(shell).toHaveAttribute('data-renderer-bootstrap', 'failed');
    expect(shell).toHaveAttribute('data-renderer-ready', 'false');
    expect(shell).toHaveAttribute('data-settings-loaded', 'false');
    expect(api.scan).not.toHaveBeenCalled();
  });

  it.each(['Root directory does not exist', 'Access denied'])('allows changing the root after startup scan fails: %s', async (message) => {
    vi.mocked(api.getInitialState).mockResolvedValue({ ...initial,
      settings: { ...initial.settings, scanOnStartup: true } });
    vi.mocked(api.scan).mockRejectedValueOnce(new Error(message));
    const { container } = render(<App />);
    expect(await screen.findByText(`启动扫描失败：${message}`)).toBeInTheDocument();
    const shell = container.querySelector('[data-renderer-bootstrap]');
    expect(shell).toHaveAttribute('data-renderer-bootstrap', 'ready');
    expect(shell).toHaveAttribute('data-renderer-ready', 'true');
    expect(shell).toHaveAttribute('data-settings-loaded', 'true');
    expect(shell).toHaveAttribute('data-startup-scan', 'failed');
    expect(shell).toHaveAttribute('data-startup-scan-count', '0');
    expect(shell).toHaveAttribute('data-bootstrap-error', '');
    expect(screen.queryByText('桌面端初始化失败')).not.toBeInTheDocument();
    expect(screen.queryByText('测试缓存')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '设置' }));
    fireEvent.change(screen.getByDisplayValue(initial.settings.rootPath), { target: { value: 'E:\\Recovered' } });
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledWith(expect.objectContaining({ rootPath: 'E:\\Recovered' })));
    await waitFor(() => expect(screen.getByRole('button', { name: '保存设置' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: '缓存库' }));
    expect(screen.getByText('测试缓存')).toBeInTheDocument();
    expect(screen.getByDisplayValue('E:\\Recovered')).toBeInTheDocument();
  });

  it('keeps the application usable after cancelling the startup scan', async () => {
    const pending = deferred<ScanResult>();
    vi.mocked(api.getInitialState).mockResolvedValue({ ...initial,
      settings: { ...initial.settings, scanOnStartup: true } });
    vi.mocked(api.scan).mockImplementationOnce(() => pending.promise);
    const { container } = render(<App />);
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    act(() => vi.mocked(api.onProgress).mock.calls[0][0]({
      requestId: 'startup', operation: 'scan', stage: 'scanning', message: 'Startup progress',
    }));
    expect(screen.getByText('Startup progress')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '取消' }));
    await waitFor(() => expect(api.cancel).toHaveBeenCalledOnce());
    await act(async () => pending.reject(new IpcError('cancelled', 'cancelled')));
    expect(screen.getByText('启动扫描已取消。')).toBeInTheDocument();
    const shell = container.querySelector('[data-renderer-bootstrap]');
    expect(shell).toHaveAttribute('data-renderer-bootstrap', 'ready');
    expect(shell).toHaveAttribute('data-startup-scan', 'cancelled');
    expect(shell).toHaveAttribute('data-bootstrap-error', '');
    expect(screen.queryByText('Startup progress')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    expect(await screen.findByText('测试缓存')).toBeInTheDocument();
    expect(api.scan).toHaveBeenCalledTimes(2);
  });

  it.each([
    ['忘记目录', { rootPath: '', rememberRootPath: false, scanOnStartup: false }, false],
    ['仅记住，不扫描', { rootPath: 'D:\\Bilibili\\download', rememberRootPath: true, scanOnStartup: false }, false],
    ['启用并立即扫描', { rootPath: 'D:\\Bilibili\\download', rememberRootPath: true, scanOnStartup: true }, true],
  ] as const)('saves the legacy-root migration choice: %s', async (buttonName, expectedPatch, scans) => {
    vi.mocked(api.getInitialState).mockResolvedValue({
      ...initial,
      settingsState: { canSave: true, sourceSchemaVersion: 1 },
      items: [],
    });

    render(<App />);
    expect(await screen.findByRole('dialog', { name: '确认旧版缓存目录' })).toHaveTextContent('D:\\Bilibili\\download');
    fireEvent.click(screen.getByRole('button', { name: buttonName }));

    await waitFor(() => expect(api.updateSettings).toHaveBeenCalledWith(expectedPatch));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: '确认旧版缓存目录' })).not.toBeInTheDocument());
    if (scans) {
      await waitFor(() => expect(api.scan).toHaveBeenCalledWith({
        rootPath: 'D:\\Bilibili\\download',
        includeIncomplete: false,
        persistSettings: false,
        offset: 0,
        pageSize: 100,
      }));
    } else {
      expect(api.scan).not.toHaveBeenCalled();
    }
  });

  it('does not scan behind the legacy settings decision when a keyword was persisted', async () => {
    vi.mocked(api.getInitialState).mockResolvedValue({
      ...initial,
      settings: { ...initial.settings, keyword: '旧搜索词' },
      settingsState: { canSave: true, sourceSchemaVersion: 1 },
      items: [],
    });

    render(<App />);
    expect(await screen.findByRole('dialog', { name: '确认旧版缓存目录' })).toBeInTheDocument();
    await act(async () => { await new Promise((resolve) => window.setTimeout(resolve, 450)); });
    expect(api.scan).not.toHaveBeenCalled();
    expect(api.search).not.toHaveBeenCalled();
  });

  it('clears stale rows and selections, then scans with saved root settings', async () => {
    const nextItem = { ...initial.items[0], id: '200', avid: '200', title: '新目录缓存' };
    vi.mocked(api.scan).mockImplementation(async (request) =>
      createCachePage(request.rootPath === 'E:\\NewCache' ? [nextItem] : initial.items));

    render(<App />);
    await screen.findByText('测试缓存');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '设置' }));
    fireEvent.change(screen.getByDisplayValue('D:\\Bilibili\\download'), { target: { value: 'E:\\NewCache' } });
    fireEvent.click(screen.getByRole('checkbox', { name: '扫描时包含下载未完成的缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));

    await waitFor(() => expect(api.scan).toHaveBeenCalledWith({
      rootPath: 'E:\\NewCache',
      includeIncomplete: true,
      persistSettings: false,
      offset: 0,
      pageSize: 100,
    }));
    expect(vi.mocked(api.scan).mock.invocationCallOrder[0]).toBeLessThan(vi.mocked(api.updateSettings).mock.invocationCallOrder[0]);
    await waitFor(() => expect(screen.getByRole('button', { name: '保存设置' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: '缓存库' }));
    expect(await screen.findByText('新目录缓存')).toBeInTheDocument();
    expect(screen.queryByText('测试缓存')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '播放' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '删除' })).toBeDisabled();
  });

  it.each(['root', 'includeIncomplete'] as const)('shows scan issues after changing settings: %s', async (change) => {
    vi.mocked(api.scan).mockResolvedValue({ ...createCachePage(), invalidEntries: 2, inaccessibleDirectories: 1,
      hasWarnings: true, issuesTruncated: true,
      issues: [{ id: 0, kind: 'InvalidEntry', path: 'damaged/entry.json', message: 'Settings scan damage' }] });
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: '设置' }));
    if (change === 'root') fireEvent.change(screen.getByDisplayValue(initial.settings.rootPath), { target: { value: 'E:\\NewCache' } });
    else fireEvent.click(screen.getByRole('checkbox', { name: '扫描时包含下载未完成的缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));
    const notice = await screen.findByText(/设置已保存。扫描完成.*损坏 2 条，无法访问 1 处/);
    expect(notice.closest('.toast')).toHaveClass('error');
    expect(screen.getByLabelText('扫描结果')).toHaveTextContent('Settings scan damage');
    expect(screen.getByText('仅展示前 100 条问题，汇总计数包含全部条目。')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '定位问题 1', hidden: true }));
    await waitFor(() => expect(api.locateScanIssue).toHaveBeenCalledWith(indexToken, 0));
    fireEvent.click(screen.getByRole('button', { name: '缓存库' }));
    expect(screen.getByLabelText('扫描结果')).toHaveTextContent('Settings scan damage');
  });

  it('shows scan issues after enabling startup scan for legacy settings', async () => {
    vi.mocked(api.getInitialState).mockResolvedValue({ ...initial,
      settingsState: { canSave: true, sourceSchemaVersion: 1 }, items: [] });
    vi.mocked(api.scan).mockResolvedValue({ ...createCachePage(), invalidEntries: 1, hasWarnings: true,
      issues: [{ id: 0, kind: 'InvalidEntry', path: 'damaged/entry.json', message: 'Legacy scan damage' }] });
    render(<App />);
    fireEvent.click(await screen.findByRole('button', { name: '启用并立即扫描' }));
    expect(await screen.findByText('Legacy scan damage')).toBeInTheDocument();
    expect(screen.getByText(/已启用启动扫描。扫描完成.*损坏 1 条/).closest('.toast')).toHaveClass('error');
  });

  it('keeps the library empty and does not scan when a saved root is blank', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    vi.mocked(api.scan).mockClear();

    fireEvent.click(screen.getByRole('button', { name: '设置' }));
    fireEvent.change(screen.getByDisplayValue('D:\\Bilibili\\download'), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));
    await waitFor(() => expect(api.updateSettings).toHaveBeenCalledWith(expect.objectContaining({ rootPath: '' })));
    await waitFor(() => expect(screen.getByRole('button', { name: '保存设置' })).not.toBeDisabled());
    expect(api.scan).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: '缓存库' }));
    expect(screen.queryByText('测试缓存')).not.toBeInTheDocument();
    expect(screen.getByText('尚未加载缓存')).toBeInTheDocument();
  });

  it('keeps the old root and rows when validating a replacement root fails', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    vi.mocked(api.scan).mockRejectedValueOnce(new Error('不是有效的 B 站缓存目录'));

    fireEvent.click(screen.getByRole('button', { name: '设置' }));
    fireEvent.change(screen.getByDisplayValue('D:\\Bilibili\\download'), { target: { value: 'E:\\Invalid' } });
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));

    expect(await screen.findByText('不是有效的 B 站缓存目录')).toBeInTheDocument();
    expect(api.updateSettings).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '缓存库' }));
    expect(screen.getByDisplayValue('D:\\Bilibili\\download')).toBeInTheDocument();
    expect(screen.getByText('测试缓存')).toBeInTheDocument();
  });

  it('drops the old index when a replacement scan succeeds but settings persistence fails', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    vi.mocked(api.updateSettings).mockRejectedValueOnce(new Error('设置写入失败'));

    fireEvent.click(screen.getByRole('button', { name: '设置' }));
    fireEvent.change(screen.getByDisplayValue('D:\\Bilibili\\download'), { target: { value: 'E:\\Replacement' } });
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));
    expect(await screen.findByText('设置写入失败')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '缓存库' }));

    expect(screen.queryByText('测试缓存')).not.toBeInTheDocument();
    expect(screen.getByText('尚未加载缓存')).toBeInTheDocument();
  });

  it('keeps a validated root for this session when remembering it is disabled', async () => {
    const nextItem = { ...initial.items[0], id: '200', avid: '200', title: '临时目录缓存' };
    vi.mocked(api.scan).mockResolvedValue(createCachePage([nextItem]));
    vi.mocked(api.updateSettings).mockImplementation(async (patch) => ({
      ...initial.settings,
      ...patch,
      rootPath: patch.rememberRootPath === false ? '' : String(patch.rootPath ?? initial.settings.rootPath),
    }));

    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('button', { name: '设置' }));
    fireEvent.change(screen.getByDisplayValue('D:\\Bilibili\\download'), { target: { value: 'E:\\Temporary' } });
    fireEvent.click(screen.getByRole('checkbox', { name: '记住缓存目录' }));
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));

    await waitFor(() => expect(api.updateSettings).toHaveBeenCalledWith(expect.objectContaining({
      rootPath: 'E:\\Temporary',
      rememberRootPath: false,
      scanOnStartup: false,
    })));
    fireEvent.click(screen.getByRole('button', { name: '缓存库' }));
    expect(await screen.findByDisplayValue('E:\\Temporary')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 临时目录缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '播放' }));
    await waitFor(() => expect(api.play).toHaveBeenCalledWith(
      'E:\\Temporary',
      [{ avid: '200' }],
      'system',
      false,
      indexToken,
    ));
  });

  it('restores the complete list when a search keyword is cleared', async () => {
    vi.mocked(api.search).mockImplementation(async (request) =>
      createCachePage(request.keyword ? [] : initial.items));
    render(<App />);
    const input = await screen.findByPlaceholderText('搜索标题、UP 主、BV 号或 AV 号');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByText('测试缓存'));
    fireEvent.click(await screen.findByRole('checkbox', { name: '选择分段 第一集' }));
    expect(screen.getByRole('button', { name: '播放' })).not.toBeDisabled();

    fireEvent.change(input, { target: { value: '没有结果' } });
    await waitFor(() => expect(api.search).toHaveBeenCalledWith(expect.objectContaining({
      indexToken,
      offset: 0,
      pageSize: 100,
      keyword: '没有结果',
    })));
    await waitFor(() => expect(screen.queryByText('测试缓存')).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: '播放' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '导出' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '删除' })).toBeDisabled();

    fireEvent.change(input, { target: { value: '' } });
    await waitFor(() => expect(api.search).toHaveBeenCalledWith(expect.objectContaining({
      indexToken,
      offset: 0,
      pageSize: 100,
      keyword: '',
    })));
    expect(await screen.findByText('测试缓存')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '播放' })).toBeDisabled();
  });

  it('uses last-write-wins search and always sends the latest input after an older request', async () => {
    const oldResult = createCachePage([{ ...initial.items[0], id: 'old', avid: '101', title: '旧搜索结果' }]);
    const latestResult = createCachePage([{ ...initial.items[0], id: 'latest', avid: '102', title: '最新搜索结果' }]);
    const oldSearch = deferred<CachePage>();
    const latestSearch = deferred<CachePage>();
    vi.mocked(api.search)
      .mockImplementationOnce(() => oldSearch.promise)
      .mockImplementationOnce(() => latestSearch.promise);

    render(<App />);
    const input = await screen.findByPlaceholderText('搜索标题、UP 主、BV 号或 AV 号');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());

    fireEvent.change(input, { target: { value: '旧条件' } });
    await waitFor(() => expect(api.search).toHaveBeenCalledWith(expect.objectContaining({
      indexToken,
      offset: 0,
      pageSize: 100,
      keyword: '旧条件',
    })));
    fireEvent.change(input, { target: { value: '最新条件' } });
    expect(api.search).toHaveBeenCalledTimes(1);

    await waitFor(() => expect(api.search).toHaveBeenCalledTimes(2));
    expect(api.search).toHaveBeenLastCalledWith(expect.objectContaining({
      indexToken,
      offset: 0,
      pageSize: 100,
      keyword: '最新条件',
    }));
    expect(screen.queryByText('旧搜索结果')).not.toBeInTheDocument();

    await act(async () => { latestSearch.resolve(latestResult); });
    await act(async () => { oldSearch.resolve(oldResult); });
    expect(await screen.findByText('最新搜索结果')).toBeInTheDocument();
    expect(screen.queryByText('旧搜索结果')).not.toBeInTheDocument();
  });

  it('drops a queued old-token search when a rescan publishes a new index', async () => {
    render(<App />);
    const input = await screen.findByPlaceholderText('搜索标题、UP 主、BV 号或 AV 号');
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    vi.mocked(api.search).mockClear();
    const replacementScan = deferred<ScanResult>();
    vi.mocked(api.scan).mockImplementationOnce(() => replacementScan.promise);

    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    fireEvent.change(input, { target: { value: '扫描期间输入' } });
    await act(async () => { await new Promise((resolve) => window.setTimeout(resolve, 400)); });
    expect(api.search).not.toHaveBeenCalled();

    await act(async () => {
      replacementScan.resolve(createCachePage(initial.items, { indexToken: 'replacement-token' }));
    });
    await waitFor(() => expect(api.search).toHaveBeenCalledWith(expect.objectContaining({
      indexToken: 'replacement-token',
      keyword: '扫描期间输入',
    })));
    expect(vi.mocked(api.search).mock.calls.every(([request]) => request.indexToken === 'replacement-token')).toBe(true);
  });

  it('loads cache details only after focus and supports the next details page', async () => {
    const laterSegment: CacheSegment = {
      ...initialSegments[0],
      id: '100:101',
      segmentKey: '101',
      pageIndex: 101,
      partName: '第一百零一集',
    };
    vi.mocked(api.getCacheDetails).mockImplementation(async (request) =>
      request.offset === 0
        ? createCacheDetails(initialSegments, { totalItems: 101, hasMore: true })
        : createCacheDetails([laterSegment], {
            offset: 100,
            totalItems: 101,
            hasMore: false,
          }));

    render(<App />);
    await screen.findByText('测试缓存');
    expect(api.getCacheDetails).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    expect(api.getCacheDetails).not.toHaveBeenCalled();

    fireEvent.click(screen.getByText('测试缓存'));
    await waitFor(() => expect(api.getCacheDetails).toHaveBeenCalledWith({
      indexToken,
      avid: '100',
      offset: 0,
      pageSize: 100,
    }));
    expect(await screen.findByText('第一集')).toBeInTheDocument();

    vi.mocked(api.cancelCacheDetails).mockClear();
    fireEvent.click(screen.getByRole('button', { name: '下一页' }));
    await waitFor(() => expect(api.cancelCacheDetails).toHaveBeenCalled());
    await waitFor(() => expect(api.getCacheDetails).toHaveBeenLastCalledWith({
      indexToken,
      avid: '100',
      offset: 100,
      pageSize: 100,
    }));
    expect(await screen.findByText('第一百零一集')).toBeInTheDocument();
  });

  it('requests the next cache page with the active index token and offset', async () => {
    const nextItem = {
      ...initialItem,
      id: '200',
      avid: '200',
      title: '第二页缓存',
    };
    vi.mocked(api.scan).mockResolvedValue(
      createCachePage(initial.items, { totalItems: 150, hasMore: true }),
    );
    vi.mocked(api.search).mockImplementation(async (request) =>
      createCachePage([nextItem], {
        offset: request.offset,
        totalItems: 150,
        hasMore: false,
      }));

    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    expect(await screen.findByLabelText('缓存分页')).toHaveTextContent('1–100 / 150');
    fireEvent.click(screen.getByRole('button', { name: '下一页' }));

    await waitFor(() => expect(api.search).toHaveBeenCalledWith(expect.objectContaining({
      indexToken,
      offset: 100,
      pageSize: 100,
      keyword: '',
    })));
    expect(await screen.findByText('第二页缓存')).toBeInTheDocument();
  });

  it('virtualizes a 200-item cache page instead of rendering every row', async () => {
    const items = Array.from({ length: 200 }, (_, itemIndex) => ({
      ...initialItem,
      id: String(itemIndex + 1),
      avid: String(itemIndex + 1),
      title: '缓存 ' + String(itemIndex + 1),
    }));
    vi.mocked(api.getInitialState).mockResolvedValue({ ...initial, items });
    const { container } = render(<App />);

    await screen.findByText('缓存 1');
    const viewport = container.querySelector('[data-virtualized="true"]');
    expect(viewport).toBeInTheDocument();
    const rows = container.querySelectorAll('[data-cache-row="true"]');
    expect(rows.length).toBeGreaterThan(0);
    expect(rows.length).toBeLessThan(50);
    expect(rows.length).toBeLessThan(items.length / 4);
  });

  it('clears focused segment selection when results are cleared', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByText('测试缓存'));
    fireEvent.click(await screen.findByRole('checkbox', { name: '选择分段 第一集' }));
    expect(screen.getByRole('button', { name: '播放' })).not.toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: '清空结果' }));
    expect(screen.queryByText('测试缓存')).not.toBeInTheDocument();
    expect(screen.getByText('没有选择缓存')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '播放' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '导出' })).toBeDisabled();
  });

  it('prevents toolbar, shortcut, and double-click reentry while an operation is busy', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    const scanCalls = vi.mocked(api.scan).mock.calls.length;
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    const pendingPlay = deferred<{ queued: number; failures: [] }>();
    vi.mocked(api.play).mockImplementation(() => pendingPlay.promise);

    fireEvent.click(screen.getByRole('button', { name: '播放' }));
    await waitFor(() => expect(api.play).toHaveBeenCalledOnce());
    expect(screen.getByRole('button', { name: '播放' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '导出' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '删除' })).toBeDisabled();
    expect(screen.getByRole('button', { name: /扫描缓存/ })).toBeDisabled();

    fireEvent.doubleClick(screen.getByText('测试缓存').closest('tr')!);
    fireEvent.keyDown(window, { key: 'F5' });
    fireEvent.keyDown(window, { key: 'e', ctrlKey: true });
    fireEvent.keyDown(window, { key: 'Delete' });
    expect(api.play).toHaveBeenCalledOnce();
    expect(api.scan).toHaveBeenCalledTimes(scanCalls);
    expect(api.exportMedia).not.toHaveBeenCalled();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await act(async () => { pendingPlay.resolve({ queued: 1, failures: [] }); });
    await waitFor(() => expect(screen.getByRole('button', { name: '播放' })).not.toBeDisabled());
  });

  it('exposes transcode-cache open, policy cleanup, and renderer-confirmed clear actions', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    expect(api.getStorage).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '存储概览' }));
    await waitFor(() => expect(api.getStorage).toHaveBeenCalledWith('D:\\Bilibili\\download'));
    await waitFor(() => expect(screen.getByRole('button', { name: /打开转码缓存目录/ })).not.toBeDisabled());

    fireEvent.click(screen.getByRole('button', { name: /打开转码缓存目录/ }));
    await waitFor(() => expect(api.openTranscodeCache).toHaveBeenCalledOnce());

    fireEvent.click(screen.getByRole('button', { name: /按策略清理/ }));
    await waitFor(() => expect(api.cleanupTranscodeCache).toHaveBeenCalledOnce());

    fireEvent.click(screen.getByRole('button', { name: /清空转码缓存/ }));
    expect(screen.getByRole('dialog', { name: '清空转码缓存' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '确认' }));
    await waitFor(() => expect(api.clearTranscodeCache).toHaveBeenCalledOnce());
  });

  it('keeps completed moves and undo after cancellation of the remaining batch', async () => {
    vi.mocked(api.scan).mockResolvedValue(createCachePage([...initial.items, { ...initialItem, id: '101', avid: '101', title: '第二项' }]));
    vi.mocked(api.getInitialState).mockResolvedValue({ ...initial,
      items: [...initial.items, { ...initial.items[0], id: '101', avid: '101', title: '第二项' }] });
    vi.mocked(api.moveToTrash).mockResolvedValue({ moved: ['100'], failed: [], cancelled: true, unprocessed: ['101'], entryIds: ['trash-100'] });
    render(<App />);
    await screen.findByText('第二项');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 第二项' }));
    fireEvent.click(screen.getByRole('button', { name: '删除' }));
    fireEvent.click(screen.getByRole('button', { name: '确认' }));
    expect(await screen.findByText(/已移动 1 项，失败 0 项，未执行 1 项/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /撤销删除/ })).toBeInTheDocument();
    expect(screen.queryByText('操作已取消。')).not.toBeInTheDocument();
  });

  it('keeps partially restored results and does not start a new scan after cancellation', async () => {
    vi.mocked(api.listTrash).mockResolvedValueOnce([
      { id: 'trash-100', avid: '100', title: '已恢复条目', sizeBytes: 1, deletedAt: null },
      { id: 'trash-101', avid: '101', title: '未恢复条目', sizeBytes: 1, deletedAt: null },
    ]).mockResolvedValue([{ id: 'trash-101', avid: '101', title: '未恢复条目', sizeBytes: 1, deletedAt: null }]);
    vi.mocked(api.restoreTrash).mockResolvedValue({ restored: ['trash-100'], failed: [], cancelled: true, unprocessed: ['trash-101'] });
    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('button', { name: '回收站' }));
    await screen.findByText('已恢复条目');
    fireEvent.click(screen.getByRole('checkbox', { name: '选择全部回收站条目' }));
    fireEvent.click(screen.getByRole('button', { name: '恢复所选' }));
    expect(await screen.findByText(/已恢复 1 项，失败 0 项，未执行 1 项/)).toBeInTheDocument();
    expect(await screen.findByText('未恢复条目')).toBeInTheDocument();
    expect(screen.queryByText('已恢复条目')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 未恢复条目' }));
    await waitFor(() => expect(screen.getByRole('button', { name: '恢复所选' })).not.toBeDisabled());
    expect(api.scan).not.toHaveBeenCalled();
  });

  it('preserves undo for committed moves when a later item fails', async () => {
    const remaining = { ...initialItem, id: '101', avid: '101', title: 'Failed move' };
    vi.mocked(api.scan).mockResolvedValueOnce(createCachePage([...initial.items, remaining]))
      .mockResolvedValue(createCachePage([remaining]));
    vi.mocked(api.getInitialState).mockResolvedValue({ ...initial,
      items: [...initial.items, { ...initialItem, id: '101', avid: '101', title: 'Failed move' }] });
    vi.mocked(api.moveToTrash).mockResolvedValue({ moved: ['100'], failed: ['101'], cancelled: false, unprocessed: [], entryIds: ['trash-100'] });
    render(<App />);
    await screen.findByText('Failed move');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 Failed move' }));
    fireEvent.click(screen.getByRole('button', { name: '删除' }));
    fireEvent.click(screen.getByRole('button', { name: '确认' }));
    const notice = await screen.findByText(/已移动 1 项，失败 1 项/);
    expect(notice.closest('.toast')).toHaveClass('error');
    expect(screen.getByRole('button', { name: /撤销删除/ })).not.toBeDisabled();
    expect(screen.queryByText('测试缓存')).not.toBeInTheDocument();
    expect(screen.queryByText(/已取消剩余操作/)).not.toBeInTheDocument();
  });

  it('keeps partial restore results and reports damage from the follow-up scan', async () => {
    vi.mocked(api.listTrash).mockResolvedValueOnce([
      { id: 'trash-100', avid: '100', title: 'Restored entry', sizeBytes: 1, deletedAt: null },
      { id: 'trash-101', avid: '101', title: 'Failed restore', sizeBytes: 1, deletedAt: null },
    ]).mockResolvedValue([{ id: 'trash-101', avid: '101', title: 'Failed restore', sizeBytes: 1, deletedAt: null }]);
    vi.mocked(api.restoreTrash).mockResolvedValue({ restored: ['trash-100'], failed: ['trash-101'], cancelled: false, unprocessed: [] });
    vi.mocked(api.scan).mockResolvedValue({ ...createCachePage(), invalidEntries: 1, hasWarnings: true,
      issues: [{ id: 0, kind: 'InvalidEntry', path: 'damaged/entry.json', message: 'Restore scan damage' }] });
    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('button', { name: '回收站' }));
    await screen.findByText('Restored entry');
    fireEvent.click(screen.getByRole('checkbox', { name: '选择全部回收站条目' }));
    fireEvent.click(screen.getByRole('button', { name: '恢复所选' }));
    expect(await screen.findByText(/已恢复 1 项，失败 1 项/)).toBeInTheDocument();
    expect(await screen.findByText('Failed restore')).toBeInTheDocument();
    expect(screen.queryByText('Restored entry')).not.toBeInTheDocument();
    expect(screen.getByText(/扫描完成.*损坏 1 条/).closest('.toast')).toHaveClass('error');
    fireEvent.click(screen.getByRole('button', { name: '缓存库' }));
    expect(screen.getByLabelText('扫描结果')).toHaveTextContent('Restore scan damage');
  });

  it('preserves completed cleanup counts when the follow-up storage refresh fails', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('button', { name: '存储概览' }));
    await waitFor(() => expect(screen.getByRole('button', { name: '按策略清理' })).not.toBeDisabled());
    vi.mocked(api.getStorage).mockRejectedValue(new Error('storage unavailable'));
    fireEvent.click(screen.getByRole('button', { name: '按策略清理' }));
    expect(await screen.findByText(/清理完成：删除 1 个文件/)).toBeInTheDocument();
    expect(screen.getByText(/文件操作结果已保留，但刷新失败/)).toBeInTheDocument();
  });

  it('shows partial cleanup cancellation with actual counts and an estimated remainder', async () => {
    vi.mocked(api.cleanupTranscodeCache).mockResolvedValue({ deletedFileCount: 2, freedBytes: 1024,
      failedFileCount: 0, remainingBytes: 2048, cancelled: true, unprocessedFileCount: 3, remainingBytesEstimated: true });
    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('button', { name: '存储概览' }));
    await waitFor(() => expect(screen.getByRole('button', { name: '按策略清理' })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: '按策略清理' }));
    expect(await screen.findByText(/清理已停止：删除 2 个文件.*未执行 3 个.*剩余约/)).toBeInTheDocument();
    expect(screen.queryByText(/清理完成：/)).not.toBeInTheDocument();
  });

  it('restores the most recent move-to-trash batch with Ctrl+Z', async () => {
    const trashEntry = {
      id: 'trash-entry-100',
      avid: '100',
      title: '测试缓存',
      sizeBytes: 32 * 1024 * 1024,
      deletedAt: '2026-08-26T01:00:00Z',
      originalPath: 'D:\\Bilibili\\download\\100',
    };
    vi.mocked(api.moveToTrash).mockResolvedValue({ moved: ['100'], failed: [], entryIds: [trashEntry.id] });
    vi.mocked(api.listTrash).mockResolvedValue([trashEntry]);
    vi.mocked(api.restoreTrash).mockResolvedValue({ restored: [trashEntry.id], failed: [] });

    render(<App />);
    await screen.findByText('测试缓存');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '删除' }));
    expect(screen.getByRole('dialog')).toHaveTextContent('包括未完成或未被索引的内容');
    fireEvent.click(screen.getByRole('button', { name: '确认' }));
    await waitFor(() => expect(api.moveToTrash).toHaveBeenCalledWith('D:\\Bilibili\\download', indexToken, [{ avid: '100' }]));
    await screen.findByText(/可按 Ctrl\+Z 撤销/);
    expect(screen.getByRole('button', { name: /撤销删除/ })).toBeInTheDocument();

    fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
    await waitFor(() => expect(api.restoreTrash).toHaveBeenCalledWith('D:\\Bilibili\\download', ['trash-entry-100']));
  });

  it('does not expose permanent purge when the host capability is unavailable', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('button', { name: '回收站' }));
    expect(screen.queryByRole('button', { name: '清空回收站' })).not.toBeInTheDocument();
    expect(screen.getByText(/当前平台暂不支持永久清理/)).toBeInTheDocument();
  });

  it('binds trash to the active root and purges the complete displayed id list', async () => {
    const entries = [
      {
        id: 'trash-entry-100', avid: '100', title: '旧缓存一', sizeBytes: 32 * 1024 * 1024,
        deletedAt: '2026-08-26T01:00:00Z', originalPath: 'D:\\Bilibili\\download\\100',
      },
      {
        id: 'trash-entry-101', avid: '101', title: '旧缓存二', sizeBytes: 16 * 1024 * 1024,
        deletedAt: '2026-08-26T02:00:00Z', originalPath: 'D:\\Bilibili\\download\\101',
      },
    ];
    vi.mocked(api.getInitialState).mockResolvedValue({
      ...initial,
      capabilities: { ...initial.capabilities, trashPurge: true },
      trash: [{ ...entries[0], title: '不应采用 InitialState 中的旧条目' }],
    });
    vi.mocked(api.listTrash).mockResolvedValue(entries);
    vi.mocked(api.purgeTrash).mockResolvedValue({ purged: entries.map((entry) => entry.id), failed: [] });

    render(<App />);
    await screen.findByText('测试缓存');
    expect(api.listTrash).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '回收站' }));
    await waitFor(() => expect(api.listTrash).toHaveBeenCalledWith('D:\\Bilibili\\download'));
    expect(await screen.findByText('旧缓存一')).toBeInTheDocument();
    expect(screen.queryByText('不应采用 InitialState 中的旧条目')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: '清空回收站' }));
    const dialog = screen.getByRole('dialog', { name: '彻底清空回收站（2 项）' });
    expect(dialog).toHaveTextContent('D:\\Bilibili\\download');
    expect(dialog).toHaveTextContent('48.0 MB');
    fireEvent.change(screen.getByLabelText('输入确认文字'), { target: { value: '永久删除' } });
    fireEvent.click(screen.getByRole('button', { name: '确认' }));
    await waitFor(() => expect(api.purgeTrash).toHaveBeenCalledWith(
      'D:\\Bilibili\\download',
      ['trash-entry-100', 'trash-entry-101'],
      '永久删除',
    ));
  });

  it('marks the host offline when the bridge reports it unavailable', async () => {
    render(<App />);
    expect(await screen.findByText('服务正常')).toBeInTheDocument();
    expect(hostUnavailableListener).not.toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    vi.mocked(api.search).mockClear();

    act(() => { hostUnavailableListener?.('Desktop Host 已退出。'); });
    expect(await screen.findByText('服务未连接')).toBeInTheDocument();
    expect(screen.getByText('Desktop Host 已退出。')).toBeInTheDocument();
    expect(screen.queryByText('测试缓存')).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText('搜索标题、UP 主、BV 号或 AV 号'), { target: { value: '旧索引不得重发' } });
    await act(async () => { await new Promise((resolve) => window.setTimeout(resolve, 450)); });
    expect(api.search).not.toHaveBeenCalled();
  });

  it('drops the old index when Host rejects a search with stale_index', async () => {
    render(<App />);
    expect(await screen.findByText('测试缓存')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    vi.mocked(api.search).mockRejectedValueOnce(new IpcError('The cache index token is missing or no longer current. Run scan again.', 'stale_index'));

    fireEvent.change(screen.getByPlaceholderText('搜索标题、UP 主、BV 号或 AV 号'), { target: { value: '触发过期索引' } });
    await waitFor(() => expect(api.search).toHaveBeenCalledOnce());
    expect(await screen.findByText('缓存索引已失效，请重新扫描。')).toBeInTheDocument();
    expect(screen.queryByText('测试缓存')).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText('搜索标题、UP 主、BV 号或 AV 号'), { target: { value: '不得重用旧索引' } });
    await act(async () => { await new Promise((resolve) => window.setTimeout(resolve, 450)); });
    expect(api.search).toHaveBeenCalledOnce();
  });

  it('keeps the Host online when a domain operation fails', async () => {
    render(<App />);
    expect(await screen.findByText('服务正常')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    vi.mocked(api.scan).mockRejectedValueOnce(new Error('缓存目录格式无效'));

    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    expect(await screen.findByText('缓存目录格式无效')).toBeInTheDocument();
    expect(screen.getByText('服务正常')).toBeInTheDocument();
    expect(screen.queryByText('服务未连接')).not.toBeInTheDocument();
  });

  it('treats cancellation as informational and keeps the Host online', async () => {
    const pendingScan = deferred<ScanResult>();
    vi.mocked(api.scan).mockImplementationOnce(() => pendingScan.promise);
    render(<App />);
    expect(await screen.findByText('服务正常')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());

    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(screen.getByRole('button', { name: '取消' })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: '取消' }));
    await waitFor(() => expect(api.cancel).toHaveBeenCalledOnce());
    await act(async () => { pendingScan.reject(new IpcError('The operation was cancelled.', 'cancelled')); });

    expect(await screen.findByText('操作已取消。')).toBeInTheDocument();
    expect(screen.getByText('服务正常')).toBeInTheDocument();
    expect(screen.queryByText('服务未连接')).not.toBeInTheDocument();
  });
});
