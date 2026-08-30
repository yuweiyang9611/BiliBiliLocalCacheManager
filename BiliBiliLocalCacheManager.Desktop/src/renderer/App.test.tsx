// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { CacheManagerApi, InitialState } from '../shared/contracts';
import { defaultSettings, emptyStorage } from '../shared/contracts';
import { App } from './App';

const initial: InitialState = {
  settings: { ...defaultSettings, rootPath: 'D:\\Bilibili\\download' },
  settingsState: { canSave: true, sourceSchemaVersion: 2 },
  items: [{
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
    }],
  }],
  storage: emptyStorage,
  trash: [],
  capabilities: { playback: true, exportMedia: true, trashPurge: false, nativeWayland: false },
};

function createApi(): CacheManagerApi {
  return {
    health: vi.fn().mockResolvedValue({ status: 'ok', version: '1.0.0' }),
    getInitialState: vi.fn().mockResolvedValue(initial),
    getSettings: vi.fn().mockResolvedValue(initial.settings),
    updateSettings: vi.fn().mockImplementation(async (patch) => ({ ...initial.settings, ...patch })),
    chooseRootDirectory: vi.fn().mockResolvedValue(null),
    scan: vi.fn().mockResolvedValue({ items: initial.items }),
    cancel: vi.fn().mockResolvedValue(true),
    search: vi.fn().mockResolvedValue(initial.items),
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
  const promise = new Promise<T>((complete) => { resolve = complete; });
  return { promise, resolve };
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
    vi.mocked(api.getInitialState).mockResolvedValue({
      ...initial,
      settings: { ...initial.settings, scanOnStartup: true },
      items: [],
    });
    render(<App />);
    await waitFor(() => expect(api.scan).toHaveBeenCalledWith({
      rootPath: 'D:\\Bilibili\\download',
      includeIncomplete: false,
      persistSettings: false,
    }));
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
    vi.mocked(api.scan).mockImplementation(async (request) => ({
      items: request.rootPath === 'E:\\NewCache' ? [nextItem] : initial.items,
    }));

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

  it('keeps a validated root for this session when remembering it is disabled', async () => {
    const nextItem = { ...initial.items[0], id: '200', avid: '200', title: '临时目录缓存' };
    vi.mocked(api.scan).mockResolvedValue({ items: [nextItem] });
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
    vi.mocked(api.search).mockImplementation(async (request) => request.keyword ? [] : initial.items);
    render(<App />);
    const input = await screen.findByPlaceholderText('搜索标题、UP 主、BV 号或 AV 号');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    await waitFor(() => expect(api.scan).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('checkbox', { name: '选择 测试缓存' }));
    fireEvent.click(screen.getByText('测试缓存'));
    fireEvent.click(screen.getByRole('checkbox', { name: '选择分段 第一集' }));
    expect(screen.getByRole('button', { name: '播放' })).not.toBeDisabled();

    fireEvent.change(input, { target: { value: '没有结果' } });
    await waitFor(() => expect(api.search).toHaveBeenCalledWith(expect.objectContaining({ keyword: '没有结果' })));
    await waitFor(() => expect(screen.queryByText('测试缓存')).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: '播放' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '导出' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '删除' })).toBeDisabled();

    fireEvent.change(input, { target: { value: '' } });
    await waitFor(() => expect(api.search).toHaveBeenCalledWith(expect.objectContaining({ keyword: '' })));
    expect(await screen.findByText('测试缓存')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '播放' })).toBeDisabled();
  });

  it('clears focused segment selection when results are cleared', async () => {
    render(<App />);
    await screen.findByText('测试缓存');
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    fireEvent.click(screen.getByText('测试缓存'));
    fireEvent.click(screen.getByRole('checkbox', { name: '选择分段 第一集' }));
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

    act(() => { hostUnavailableListener?.('Desktop Host 已退出。'); });
    expect(await screen.findByText('服务未连接')).toBeInTheDocument();
    expect(screen.getByText('Desktop Host 已退出。')).toBeInTheDocument();
  });

  it('does not keep a stale healthy status after a host call fails', async () => {
    render(<App />);
    expect(await screen.findByText('服务正常')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: /扫描缓存/ })).not.toBeDisabled());
    vi.mocked(api.scan).mockRejectedValueOnce(new Error('Host connection closed'));

    fireEvent.click(screen.getByRole('button', { name: /扫描缓存/ }));
    expect(await screen.findByText('服务未连接')).toBeInTheDocument();
    expect(screen.getByText('Host connection closed')).toBeInTheDocument();
  });
});
