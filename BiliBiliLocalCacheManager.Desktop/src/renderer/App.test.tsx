// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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
  protocolVersion: 2,
  settings: { ...defaultSettings, rootPath: 'D:\\Bilibili\\download' },
  settingsState: { canSave: true, sourceSchemaVersion: 2 },
  items: [initialItem],
  storage: emptyStorage,
  trash: [],
  capabilities: { playback: true, exportMedia: true, cacheDetails: true, trashPurge: false, nativeWayland: false },
};

function createCachePage(
  items: CacheEntry[] = initial.items,
  overrides: Partial<CachePage> = {},
): CachePage {
  return {
    indexToken,
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
  return {
    health: vi.fn().mockResolvedValue({ protocolVersion: 2, status: 'ok', version: '1.0.0' }),
    getInitialState: vi.fn().mockResolvedValue(initial),
    getSettings: vi.fn().mockResolvedValue(initial.settings),
    updateSettings: vi.fn().mockImplementation(async (patch) => ({ ...initial.settings, ...patch })),
    chooseRootDirectory: vi.fn().mockResolvedValue(null),
    scan: vi.fn().mockResolvedValue(createCachePage()),
    cancel: vi.fn().mockResolvedValue(true),
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
    play: vi.fn().mockResolvedValue({ queued: 1 }),
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

    await act(async () => { oldSearch.resolve(oldResult); });
    expect(screen.queryByText('旧搜索结果')).not.toBeInTheDocument();
    await waitFor(() => expect(api.search).toHaveBeenCalledTimes(2));
    expect(api.search).toHaveBeenLastCalledWith(expect.objectContaining({
      indexToken,
      offset: 0,
      pageSize: 100,
      keyword: '最新条件',
    }));
    expect(screen.queryByText('旧搜索结果')).not.toBeInTheDocument();

    await act(async () => { latestSearch.resolve(latestResult); });
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
    const pendingPlay = deferred<{ queued: number }>();
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

    await act(async () => { pendingPlay.resolve({ queued: 1 }); });
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

  it('restores the most recent move-to-trash batch with Ctrl+Z', async () => {
    const trashEntry = {
      id: 'trash-entry-100',
      avid: '100',
      title: '测试缓存',
      sizeBytes: 32 * 1024 * 1024,
      deletedAt: '2026-08-26T01:00:00Z',
      originalPath: 'D:\\Bilibili\\download\\100',
    };
    vi.mocked(api.moveToTrash).mockResolvedValue({ moved: ['100'], failed: [] });
    vi.mocked(api.listTrash).mockResolvedValue([trashEntry]);
    vi.mocked(api.restoreTrash).mockResolvedValue({ restored: [trashEntry.id], failed: [] });

    render(<App />);
    await screen.findByText('测试缓存');
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByRole('button', { name: '删除' }));
    fireEvent.click(screen.getByRole('button', { name: '确认' }));
    await waitFor(() => expect(api.moveToTrash).toHaveBeenCalledWith('D:\\Bilibili\\download', ['100']));
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
    fireEvent.click(screen.getByRole('button', { name: '确认' }));
    await waitFor(() => expect(api.purgeTrash).toHaveBeenCalledWith(
      'D:\\Bilibili\\download',
      ['trash-entry-100', 'trash-entry-101'],
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
    vi.mocked(api.search).mockRejectedValueOnce(new Error('The cache index token is missing or no longer current. Run scan again.'));

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
    await act(async () => { pendingScan.reject(new Error('The operation was cancelled.')); });

    expect(await screen.findByText('操作已取消。')).toBeInTheDocument();
    expect(screen.getByText('服务正常')).toBeInTheDocument();
    expect(screen.queryByText('服务未连接')).not.toBeInTheDocument();
  });
});
