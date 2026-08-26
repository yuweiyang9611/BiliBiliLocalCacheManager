import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import type {
  AppSettings,
  ArtifactCleanupResult,
  CacheEntry,
  CacheSegment,
  DesktopCapabilities,
  DesktopInfo,
  HostHealth,
  HostProgress,
  PlayerPreference,
  SelectionTarget,
  StorageSnapshot,
  TrashEntry,
} from '../shared/contracts';
import { defaultSettings, emptyStorage } from '../shared/contracts';
import { Icon, type IconName } from './components/Icon';

type Page = 'library' | 'storage' | 'trash' | 'settings' | 'diagnostics';
type Notice = { id: number; kind: 'success' | 'error' | 'info'; message: string };
type Activity = { time: Date; kind: 'success' | 'error' | 'info'; message: string };
type UndoDeleteBatch = { rootPath: string; avids: string[] };

const navigation: Array<{ id: Page; label: string; icon: IconName }> = [
  { id: 'library', label: '缓存库', icon: 'library' },
  { id: 'storage', label: '存储概览', icon: 'storage' },
  { id: 'trash', label: '回收站', icon: 'trash' },
  { id: 'settings', label: '设置', icon: 'settings' },
  { id: 'diagnostics', label: '诊断', icon: 'diagnostics' },
];

export function App() {
  const [page, setPage] = useState<Page>('library');
  const [settings, setSettings] = useState<AppSettings>(defaultSettings);
  const [draftSettings, setDraftSettings] = useState<AppSettings>(defaultSettings);
  const [items, setItems] = useState<CacheEntry[]>([]);
  const [storage, setStorage] = useState<StorageSnapshot>(emptyStorage);
  const [trash, setTrash] = useState<TrashEntry[]>([]);
  const [health, setHealth] = useState<HostHealth | null>(null);
  const [capabilities, setCapabilities] = useState<DesktopCapabilities>({ playback: true, exportMedia: true, trashPurge: false, nativeWayland: false });
  const [desktop, setDesktop] = useState<DesktopInfo | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [focusedId, setFocusedId] = useState<string | null>(null);
  const [selectedSegmentIds, setSelectedSegmentIds] = useState<Set<string>>(new Set());
  const [selectedTrashIds, setSelectedTrashIds] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState<string | null>('正在连接 Desktop Host…');
  const [initialized, setInitialized] = useState(false);
  const [progress, setProgress] = useState<HostProgress | null>(null);
  const [notices, setNotices] = useState<Notice[]>([]);
  const [activities, setActivities] = useState<Activity[]>([]);
  const [confirm, setConfirm] = useState<{ title: string; body: string; destructive?: boolean; action(): void } | null>(null);
  const [undoDeleteBatch, setUndoDeleteBatch] = useState<UndoDeleteBatch | null>(null);
  const noticeId = useRef(0);
  const searchInput = useRef<HTMLInputElement>(null);
  const searchWasActive = useRef(false);
  const operationInFlight = useRef(true);

  const notify = useCallback((kind: Notice['kind'], message: string) => {
    const id = ++noticeId.current;
    setNotices((current) => [...current.slice(-3), { id, kind, message }]);
    setActivities((current) => [{ time: new Date(), kind, message }, ...current].slice(0, 50));
    window.setTimeout(() => setNotices((current) => current.filter((item) => item.id !== id)), 4_500);
  }, []);

  const replaceLibraryItems = useCallback((nextItems: CacheEntry[] = []) => {
    setItems(nextItems);
    setSelectedIds(new Set());
    setFocusedId(null);
    setSelectedSegmentIds(new Set());
  }, []);

  const run = useCallback(async <T,>(label: string, operation: () => Promise<T>): Promise<T | undefined> => {
    if (operationInFlight.current) return undefined;
    operationInFlight.current = true;
    setBusy(label);
    try {
      return await operation();
    } catch (error) {
      setHealth(null);
      notify('error', describeError(error));
      return undefined;
    } finally {
      operationInFlight.current = false;
      setBusy(null);
      setProgress(null);
    }
  }, [notify]);

  useEffect(() => {
    const unsubscribeProgress = window.cacheManager.onProgress((value) => setProgress(value));
    const unsubscribeUnavailable = window.cacheManager.onHostUnavailable((message) => {
      setHealth(null);
      notify('error', message);
    });
    void (async () => {
      try {
        const [initial, hostHealth, info] = await Promise.all([
          window.cacheManager.getInitialState(),
          window.cacheManager.health(),
          window.cacheManager.getDesktopInfo(),
        ]);
        const loadedSettings = { ...defaultSettings, ...(initial.settings ?? {}) };
        setSettings(loadedSettings);
        setDraftSettings(loadedSettings);
        setItems(initial.items ?? []);
        setStorage({ ...emptyStorage, ...(initial.storage ?? {}) });
        setTrash(initial.trash ?? []);
        setCapabilities(initial.capabilities ?? { playback: true, exportMedia: true, trashPurge: false, nativeWayland: false });
        setHealth(hostHealth);
        setDesktop(info);
        notify('success', 'Desktop Host 已连接。');
        setInitialized(true);

        if (loadedSettings.rootPath.trim()) {
          setBusy('正在自动扫描缓存…');
          try {
            const result = await window.cacheManager.scan({
              rootPath: loadedSettings.rootPath,
              includeIncomplete: loadedSettings.includeIncomplete,
            });
            replaceLibraryItems(result.items ?? []);
            notify('success', `已自动扫描记住的目录，共发现 ${result.items?.length ?? 0} 条缓存。`);
            try { setStorage(await window.cacheManager.getStorage()); }
            catch { setHealth(null); }
          } catch (error) {
            setHealth(null);
            notify('error', `自动扫描失败：${describeError(error)}`);
          }
        }
      } catch (error) {
        setHealth(null);
        notify('error', describeError(error));
      } finally {
        operationInFlight.current = false;
        setBusy(null);
        setInitialized(true);
      }
    })();
    return () => { unsubscribeProgress(); unsubscribeUnavailable(); };
  }, [notify, replaceLibraryItems]);

  const focusedItem = useMemo(
    () => items.find((item) => item.id === focusedId || item.avid === focusedId) ?? null,
    [focusedId, items],
  );
  const targets = useMemo<SelectionTarget[]>(() => {
    if (focusedItem && selectedSegmentIds.size > 0) {
      return [{
        avid: focusedItem.avid,
        pageIndexes: focusedItem.segments.filter((segment) => selectedSegmentIds.has(segment.id)).map((segment) => segment.pageIndex),
      }];
    }
    return items.filter((item) => selectedIds.has(item.id)).map((item) => ({ avid: item.avid }));
  }, [focusedItem, items, selectedIds, selectedSegmentIds]);

  const browse = useCallback(async () => {
    const saved = await run('正在选择缓存目录…', async () => {
      const rootPath = await window.cacheManager.chooseRootDirectory(settings.rootPath);
      if (!rootPath) return null;
      return window.cacheManager.updateSettings({ rootPath });
    });
    if (!saved) return;
    setSettings(saved);
    setDraftSettings(saved);
    setUndoDeleteBatch(null);
    replaceLibraryItems();
    if (!saved.rootPath.trim()) return;
    const result = await run('正在扫描新目录…', () => window.cacheManager.scan({
      rootPath: saved.rootPath,
      includeIncomplete: saved.includeIncomplete,
    }));
    if (!result) return;
    replaceLibraryItems(result.items ?? []);
    notify('success', `目录已保存并扫描，共发现 ${result.items?.length ?? 0} 条缓存。`);
    void window.cacheManager.getStorage().then(setStorage).catch(() => setHealth(null));
  }, [notify, replaceLibraryItems, run, settings.rootPath]);

  const scan = useCallback(async () => {
    if (!settings.rootPath.trim()) { notify('error', '请先选择 B 站缓存根目录。'); return; }
    const result = await run('正在扫描缓存…', () => window.cacheManager.scan({
      rootPath: settings.rootPath,
      includeIncomplete: settings.includeIncomplete,
    }));
    if (!result) return;
    replaceLibraryItems(result.items ?? []);
    notify('success', `扫描完成，共发现 ${result.items?.length ?? 0} 条缓存。`);
    void window.cacheManager.getStorage().then(setStorage).catch(() => setHealth(null));
  }, [notify, replaceLibraryItems, run, settings.includeIncomplete, settings.rootPath]);

  const search = useCallback(async () => {
    const result = await run('正在筛选…', () => window.cacheManager.search({
      keyword: settings.keyword.trim(),
      matchMode: settings.matchMode,
      splitKeywords: settings.splitKeywords,
      anyKeywords: settings.anyKeywords,
      includePartName: settings.includePartName,
      includeOwnerName: settings.includeOwnerName,
      includeBvid: settings.includeBvid,
      includeAvid: settings.includeAvid,
      caseSensitive: settings.caseSensitive,
    }));
    if (result) replaceLibraryItems(result);
  }, [replaceLibraryItems, run, settings]);

  useEffect(() => {
    const hasKeyword = Boolean(settings.keyword.trim());
    if (!hasKeyword && !searchWasActive.current) return;
    const timer = window.setTimeout(() => {
      searchWasActive.current = hasKeyword;
      void search();
    }, 350);
    return () => window.clearTimeout(timer);
  }, [search, settings.keyword]);

  const updateSetting = <K extends keyof AppSettings>(key: K, value: AppSettings[K]) => {
    setSettings((current) => ({ ...current, [key]: value }));
  };

  const play = useCallback(async (explicitTargets?: SelectionTarget[]) => {
    const requestedTargets = explicitTargets ?? targets;
    if (requestedTargets.length === 0) { notify('info', '请先选择缓存或分段。'); return; }
    const result = await run('正在准备播放…', () => window.cacheManager.play(requestedTargets, settings.playerPreference));
    if (result) notify('success', `已将 ${result.queued} 个页面交给播放器。`);
  }, [notify, run, settings.playerPreference, targets]);

  const exportMedia = useCallback(async () => {
    if (targets.length === 0) { notify('info', '请先选择要导出的缓存或分段。'); return; }
    const title = targets.length === 1 && focusedItem ? safeName(focusedItem.title) : `缓存导出-${dateStamp()}`;
    const result = await run('正在导出 MP4…', () => window.cacheManager.exportMedia(targets, `${title}.mp4`));
    if (result) notify('success', `已导出：${result.outputPath}`);
  }, [focusedItem, notify, run, targets]);

  const moveToTrash = useCallback(() => {
    const avids = items.filter((item) => selectedIds.has(item.id)).map((item) => item.avid);
    if (avids.length === 0) { notify('info', '请先选择要删除的缓存。'); return; }
    setConfirm({
      title: `移入回收站（${avids.length} 项）`,
      body: '所选缓存将移动到应用回收站，之后仍可恢复。正在播放或导出的项目请先停止操作。',
      destructive: true,
      action: () => { void (async () => {
        const completed = await run('正在移动到回收站…', async () => {
          const result = await window.cacheManager.moveToTrash(avids);
          const [nextTrash, nextStorage] = await Promise.all([
            window.cacheManager.listTrash(),
            window.cacheManager.getStorage(),
          ]);
          return { result, nextTrash, nextStorage };
        });
        if (!completed) return;
        setItems((current) => current.filter((item) => !completed.result.moved.includes(item.avid)));
        setSelectedIds(new Set());
        setFocusedId(null);
        setSelectedSegmentIds(new Set());
        setTrash(completed.nextTrash);
        setStorage(completed.nextStorage);
        setUndoDeleteBatch(completed.result.moved.length > 0
          ? { rootPath: settings.rootPath.trim(), avids: completed.result.moved }
          : null);
        notify(completed.result.failed.length ? 'error' : 'success', `已移动 ${completed.result.moved.length} 项，失败 ${completed.result.failed.length} 项。${completed.result.moved.length ? '可按 Ctrl+Z 撤销。' : ''}`);
      })(); },
    });
  }, [items, notify, run, selectedIds, settings.rootPath]);

  useEffect(() => {
    setUndoDeleteBatch((current) => current && current.rootPath !== settings.rootPath.trim() ? null : current);
  }, [settings.rootPath]);

  const undoLastDelete = useCallback(async () => {
    const batch = undoDeleteBatch;
    if (!batch) { notify('info', '没有可撤销的删除操作。'); return; }
    if (batch.rootPath !== settings.rootPath.trim()) {
      setUndoDeleteBatch(null);
      notify('info', '缓存根目录已变化，不能撤销之前目录中的删除。');
      return;
    }

    const completed = await run('正在撤销删除…', async () => {
      const entries = await window.cacheManager.listTrash();
      const requestedAvids = new Set(batch.avids);
      const newestByAvid = new Map<string, TrashEntry>();
      for (const entry of [...entries].sort((left, right) => trashTime(right) - trashTime(left))) {
        if (requestedAvids.has(entry.avid) && !newestByAvid.has(entry.avid)) newestByAvid.set(entry.avid, entry);
      }
      const entryIds = batch.avids.flatMap((avid) => {
        const entry = newestByAvid.get(avid);
        return entry ? [entry.id] : [];
      });
      const restoreResult = entryIds.length
        ? await window.cacheManager.restoreTrash(entryIds)
        : { restored: [], failed: [] };
      const [nextTrash, nextStorage, scanResult] = await Promise.all([
        window.cacheManager.listTrash(),
        window.cacheManager.getStorage(),
        restoreResult.restored.length
          ? window.cacheManager.scan({ rootPath: settings.rootPath, includeIncomplete: settings.includeIncomplete })
          : Promise.resolve<{ items: CacheEntry[] } | null>(null),
      ]);
      return {
        restoreResult,
        nextTrash,
        nextStorage,
        scanResult,
        missingCount: batch.avids.length - entryIds.length,
      };
    });
    if (!completed) return;
    setUndoDeleteBatch(null);
    setTrash(completed.nextTrash);
    setStorage(completed.nextStorage);
    setSelectedTrashIds(new Set());
    if (completed.scanResult) {
      replaceLibraryItems(completed.scanResult.items ?? []);
    }
    const failedCount = completed.restoreResult.failed.length + completed.missingCount;
    notify(failedCount ? 'error' : 'success', `已撤销 ${completed.restoreResult.restored.length} 项删除，失败 ${failedCount} 项。`);
  }, [notify, replaceLibraryItems, run, settings.includeIncomplete, settings.rootPath, undoDeleteBatch]);

  const refreshStorage = useCallback(async () => {
    const value = await run('正在统计存储…', () => window.cacheManager.getStorage());
    if (value) { setStorage(value); notify('success', '存储统计已刷新。'); }
  }, [notify, run]);

  const cleanupTranscodeCache = useCallback(async () => {
    const completed = await run('正在按策略清理转码缓存…', async () => {
      const result = await window.cacheManager.cleanupTranscodeCache();
      return { result, snapshot: await window.cacheManager.getStorage() };
    });
    if (!completed) return;
    setStorage(completed.snapshot);
    notify(completed.result.failedFileCount ? 'error' : 'success', artifactCleanupMessage('清理完成', completed.result));
  }, [notify, run]);

  const openTranscodeCache = useCallback(async () => {
    const opened = await run('正在打开转码缓存目录…', () => window.cacheManager.openTranscodeCache());
    if (opened) notify('success', '已打开受管转码缓存目录。');
  }, [notify, run]);

  const requestClearTranscodeCache = useCallback(() => {
    setConfirm({
      title: '清空转码缓存',
      body: '将清空应用管理的转码产物，不会删除 B 站原始缓存。确认后系统还会再询问一次。',
      destructive: true,
      action: () => { void (async () => {
        const completed = await run('正在清空转码缓存…', async () => {
          const result = await window.cacheManager.clearTranscodeCache();
          if (!result) return null;
          return { result, snapshot: await window.cacheManager.getStorage() };
        });
        if (!completed) return;
        setStorage(completed.snapshot);
        notify(completed.result.failedFileCount ? 'error' : 'success', artifactCleanupMessage('清空完成', completed.result));
      })(); },
    });
  }, [notify, run]);

  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      if (event.key === 'F5') { event.preventDefault(); if (!busy) void scan(); }
      if (event.key === 'Escape' && busy) { event.preventDefault(); void window.cacheManager.cancel(); }
      if (event.ctrlKey && event.key.toLowerCase() === 'f') { event.preventDefault(); setPage('library'); window.setTimeout(() => searchInput.current?.focus(), 0); }
      if (event.ctrlKey && event.key.toLowerCase() === 'e') { event.preventDefault(); if (!busy) void exportMedia(); }
      if (event.ctrlKey && !event.shiftKey && event.key.toLowerCase() === 'z' && !isEditable(event.target)) { event.preventDefault(); if (!busy) void undoLastDelete(); }
      if (event.key === 'Delete' && page === 'library' && !isEditable(event.target) && !busy) moveToTrash();
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [busy, exportMedia, moveToTrash, page, scan, undoLastDelete]);

  return (
    <div className="app-shell" data-renderer-ready={initialized ? 'true' : 'false'}>
      <aside className="sidebar">
        <div className="brand"><div className="brand-mark"><Icon name="film" /></div><div><strong>缓存管理器</strong><span>Desktop</span></div></div>
        <nav aria-label="主导航">
          {navigation.map((item) => (
            <button key={item.id} className={page === item.id ? 'nav-item active' : 'nav-item'} onClick={() => setPage(item.id)}>
              <Icon name={item.icon} /><span>{item.label}</span>
              {item.id === 'trash' && trash.length > 0 && <b>{trash.length}</b>}
            </button>
          ))}
        </nav>
        <div className="sidebar-footer">
          <span className={health?.status === 'ok' ? 'connection online' : health?.status === 'degraded' ? 'connection degraded' : 'connection offline'} />
          <div><strong>{health?.status === 'ok' ? '服务正常' : health?.status === 'degraded' ? '服务可用，环境待检查' : '服务未连接'}</strong><small>{desktop ? `Electron ${desktop.electronVersion} · ${desktop.displayBackend}` : '等待运行时信息'}</small></div>
        </div>
      </aside>

      <main className="workspace">
        <header className="topbar">
          <div><h1>{navigation.find((item) => item.id === page)?.label}</h1><p>{pageSubtitle(page)}</p></div>
          <div className="top-actions">
            {undoDeleteBatch && <button className="button secondary" onClick={() => void undoLastDelete()} disabled={Boolean(busy)}><Icon name="restore" />撤销删除 <kbd>Ctrl+Z</kbd></button>}
            {busy && <button className="button ghost" onClick={() => void window.cacheManager.cancel()}><Icon name="stop" />取消</button>}
            <button className="button primary" onClick={() => void scan()} disabled={Boolean(busy)}><Icon name="scan" />扫描缓存 <kbd>F5</kbd></button>
          </div>
        </header>

        {progress && <div className="operation-progress"><div style={{ width: `${clamp(progress.percentage ?? 12, 2, 100)}%` }} /><span>{progress.message ?? progress.stage}</span></div>}

        <section className="page-content">
          {page === 'library' && <LibraryPage
            settings={settings} updateSetting={updateSetting} browse={browse} search={search} searchInput={searchInput}
            items={items} selectedIds={selectedIds} setSelectedIds={setSelectedIds} focusedId={focusedId}
            focus={(item) => { setFocusedId(item.id); setSelectedSegmentIds(new Set()); }} focusedItem={focusedItem}
            selectedSegmentIds={selectedSegmentIds} setSelectedSegmentIds={setSelectedSegmentIds}
            busy={Boolean(busy)} play={play} exportMedia={exportMedia} moveToTrash={moveToTrash}
            clear={() => replaceLibraryItems()}
          />}
          {page === 'storage' && <StoragePage
            storage={storage}
            settings={settings}
            busy={Boolean(busy)}
            refresh={refreshStorage}
            cleanup={cleanupTranscodeCache}
            clear={requestClearTranscodeCache}
            open={openTranscodeCache}
          />}
          {page === 'trash' && <TrashPage entries={trash} selected={selectedTrashIds} setSelected={setSelectedTrashIds} busy={Boolean(busy)} canPurge={capabilities.trashPurge} restore={() => {
            if (!selectedTrashIds.size) return notify('info', '请选择要恢复的条目。');
            void (async () => {
              const completed = await run('正在恢复缓存…', async () => {
                const result = await window.cacheManager.restoreTrash([...selectedTrashIds]);
                const [nextTrash, nextStorage, scanResult] = await Promise.all([
                  window.cacheManager.listTrash(),
                  window.cacheManager.getStorage(),
                  result.restored.length && settings.rootPath.trim()
                    ? window.cacheManager.scan({ rootPath: settings.rootPath, includeIncomplete: settings.includeIncomplete })
                    : Promise.resolve<{ items: CacheEntry[] } | null>(null),
                ]);
                return { result, nextTrash, nextStorage, scanResult };
              });
              if (!completed) return;
              setTrash(completed.nextTrash); setStorage(completed.nextStorage); setSelectedTrashIds(new Set()); setUndoDeleteBatch(null);
              if (completed.scanResult) replaceLibraryItems(completed.scanResult.items ?? []);
              notify(completed.result.failed.length ? 'error' : 'success', `已恢复 ${completed.result.restored.length} 项。`);
            })();
          }} purge={(all) => setConfirm({
            title: all ? '彻底清空回收站' : `永久删除（${selectedTrashIds.size} 项）`,
            body: '此操作会永久删除文件，无法撤销。请确认这些缓存不再需要。', destructive: true,
            action: () => { void (async () => {
              const ids = all ? undefined : [...selectedTrashIds];
              if (!all && ids!.length === 0) { notify('info', '请选择要永久删除的条目。'); return; }
              const completed = await run('正在永久删除…', async () => {
                const result = await window.cacheManager.purgeTrash(ids);
                const [nextTrash, nextStorage] = await Promise.all([
                  window.cacheManager.listTrash(),
                  window.cacheManager.getStorage(),
                ]);
                return { result, nextTrash, nextStorage };
              });
              if (!completed) return;
              setTrash(completed.nextTrash); setStorage(completed.nextStorage); setSelectedTrashIds(new Set());
              notify(completed.result.failed.length ? 'error' : 'success', `已永久删除 ${completed.result.purged.length} 项。`);
            })(); },
          })} />}
          {page === 'settings' && <SettingsPage value={draftSettings} setValue={setDraftSettings} browse={async () => {
            const value = await run('正在选择缓存目录…', () => window.cacheManager.chooseRootDirectory(draftSettings.rootPath));
            if (value) setDraftSettings((current) => ({ ...current, rootPath: value }));
          }} save={async () => {
            const value = await run('正在保存设置…', () => window.cacheManager.updateSettings(draftSettings));
            if (!value) return;
            setSettings(value);
            setDraftSettings(value);
            setUndoDeleteBatch(null);
            replaceLibraryItems();
            if (!value.rootPath.trim()) {
              notify('success', '设置已保存；缓存根目录为空，列表已清空。');
              return;
            }
            const result = await run('正在按新设置扫描…', () => window.cacheManager.scan({
              rootPath: value.rootPath,
              includeIncomplete: value.includeIncomplete,
            }));
            if (!result) return;
            replaceLibraryItems(result.items ?? []);
            notify('success', `设置已保存并重新扫描，共发现 ${result.items?.length ?? 0} 条缓存。`);
          }} busy={Boolean(busy)} />}
          {page === 'diagnostics' && <DiagnosticsPage health={health} desktop={desktop} activities={activities} refresh={async () => {
            const value = await run('正在检查运行环境…', () => window.cacheManager.health());
            if (value) { setHealth(value); notify(value.status === 'ok' ? 'success' : 'error', '运行环境检查完成。'); }
          }} exportReport={async () => {
            const value = await run('正在导出诊断报告…', () => window.cacheManager.exportDiagnostics(`BLCM-diagnostics-${dateStamp()}.zip`));
            if (value) notify('success', `诊断报告已导出：${value.outputPath}`);
          }} busy={Boolean(busy)} />}
        </section>

        <footer className="statusbar"><span>{busy ?? (items.length ? `当前显示 ${items.length} 条缓存，已选 ${selectedIds.size} 条 · ${formatBytes(selectedBytes(items, selectedIds))}` : '就绪')}</span><span>F5 扫描 · Ctrl+F 搜索 · Ctrl+Z 撤销 · Ctrl+E 导出 · Esc 取消</span></footer>
      </main>

      <div className="toast-stack" aria-live="polite">{notices.map((notice) => <div key={notice.id} className={`toast ${notice.kind}`}><Icon name={notice.kind === 'error' ? 'warning' : 'check'} /><span>{notice.message}</span></div>)}</div>
      {confirm && <Modal title={confirm.title} onClose={() => setConfirm(null)}><p>{confirm.body}</p><div className="modal-actions"><button className="button ghost" onClick={() => setConfirm(null)}>取消</button><button className={confirm.destructive ? 'button danger' : 'button primary'} onClick={() => { const action = confirm.action; setConfirm(null); action(); }}>确认</button></div></Modal>}
    </div>
  );
}

interface LibraryProps {
  settings: AppSettings;
  updateSetting<K extends keyof AppSettings>(key: K, value: AppSettings[K]): void;
  browse(): Promise<void>;
  search(): Promise<void>;
  searchInput: React.RefObject<HTMLInputElement | null>;
  items: CacheEntry[];
  selectedIds: Set<string>;
  setSelectedIds(value: Set<string>): void;
  focusedId: string | null;
  focus(item: CacheEntry): void;
  focusedItem: CacheEntry | null;
  selectedSegmentIds: Set<string>;
  setSelectedSegmentIds(value: Set<string>): void;
  busy: boolean;
  play(targets?: SelectionTarget[]): Promise<void>;
  exportMedia(): Promise<void>;
  moveToTrash(): void;
  clear(): void;
}

function LibraryPage(props: LibraryProps) {
  const { settings, updateSetting } = props;
  return <div className="library-layout">
    <section className="card root-card">
      <div className="field grow"><label htmlFor="root-path">缓存根目录</label><div className="input-action"><input id="root-path" value={settings.rootPath} onChange={(event) => updateSetting('rootPath', event.target.value)} placeholder="选择 B 站 download 缓存目录" /><button className="icon-button" aria-label="浏览缓存目录" onClick={() => void props.browse()} disabled={props.busy}><Icon name="folder" /></button></div></div>
      <label className="toggle"><input type="checkbox" checked={settings.includeIncomplete} onChange={(event) => updateSetting('includeIncomplete', event.target.checked)} /><span />包含未完成缓存</label>
    </section>
    <section className="card search-card">
      <div className="search-box"><Icon name="search" /><input ref={props.searchInput} value={settings.keyword} onChange={(event) => updateSetting('keyword', event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !props.busy) void props.search(); }} placeholder="搜索标题、UP 主、BV 号或 AV 号" /></div>
      <select aria-label="匹配方式" value={settings.matchMode} onChange={(event) => updateSetting('matchMode', event.target.value as AppSettings['matchMode'])}><option value="contains">包含</option><option value="prefix">前缀</option><option value="exact">精确</option></select>
      <button className="button secondary" onClick={() => void props.search()} disabled={props.busy}>筛选</button>
      <details className="filter-menu"><summary>高级筛选</summary><div className="filter-popover">
        <Check label="分词" checked={settings.splitKeywords} onChange={(value) => updateSetting('splitKeywords', value)} />
        <Check label="任意关键字" checked={settings.anyKeywords} onChange={(value) => updateSetting('anyKeywords', value)} />
        <Check label="分段名" checked={settings.includePartName} onChange={(value) => updateSetting('includePartName', value)} />
        <Check label="UP 主" checked={settings.includeOwnerName} onChange={(value) => updateSetting('includeOwnerName', value)} />
        <Check label="BV 号" checked={settings.includeBvid} onChange={(value) => updateSetting('includeBvid', value)} />
        <Check label="AV 号" checked={settings.includeAvid} onChange={(value) => updateSetting('includeAvid', value)} />
        <Check label="大小写敏感" checked={settings.caseSensitive} onChange={(value) => updateSetting('caseSensitive', value)} />
      </div></details>
    </section>
    <section className="card cache-panel">
      <div className="panel-heading"><div><h2>缓存列表</h2><span>{props.items.length} 项</span></div><div className="toolbar"><button className="button ghost" onClick={() => void props.play()} disabled={props.busy || (!props.selectedIds.size && !props.selectedSegmentIds.size)}><Icon name="play" />播放</button><button className="button ghost" onClick={() => void props.exportMedia()} disabled={props.busy || (!props.selectedIds.size && !props.selectedSegmentIds.size)}><Icon name="export" />导出</button><button className="button ghost danger-text" onClick={props.moveToTrash} disabled={props.busy || !props.selectedIds.size}><Icon name="delete" />删除</button><button className="button ghost" onClick={props.clear} disabled={props.busy}>清空结果</button></div></div>
      <div className="table-scroll cache-table"><table><thead><tr><th className="check-cell"><input aria-label="选择全部缓存" type="checkbox" checked={props.items.length > 0 && props.selectedIds.size === props.items.length} onChange={(event) => props.setSelectedIds(event.target.checked ? new Set(props.items.map((item) => item.id)) : new Set())} /></th><th>视频</th><th>UP 主</th><th>标识</th><th>时长</th><th>分段</th><th>大小</th><th>状态</th><th>更新时间</th></tr></thead>
      <tbody>{props.items.map((item) => <tr key={item.id} className={props.focusedId === item.id ? 'focused' : ''} onClick={() => props.focus(item)} onDoubleClick={() => { if (props.busy) return; props.setSelectedIds(new Set([item.id])); void props.play([{ avid: item.avid }]); }}><td className="check-cell" onClick={(event) => event.stopPropagation()}><input aria-label={`选择 ${item.title}`} type="checkbox" checked={props.selectedIds.has(item.id)} onChange={() => props.setSelectedIds(toggleSet(props.selectedIds, item.id))} /></td><td><strong className="title-cell">{item.title || '未命名缓存'}</strong><small>av{item.avid}</small></td><td>{item.ownerName || '—'}</td><td><code>{item.bvid || '—'}</code></td><td>{formatDuration(item.durationSeconds)}</td><td>{item.segmentCount}</td><td>{formatBytes(item.sizeBytes)}</td><td><span className={item.isAllCompleted ? 'badge success' : 'badge warning'}>{item.isAllCompleted ? '完整' : '未完成'}</span></td><td>{formatDate(item.lastUpdated)}</td></tr>)}</tbody></table>
      {!props.items.length && <Empty icon="library" title="尚未加载缓存" body="选择缓存根目录后点击“扫描缓存”，这里会显示可播放与可导出的缓存。" />}</div>
    </section>
    <section className="card segment-panel"><div className="panel-heading"><div><h2>分段详情</h2><span>{props.focusedItem ? `${props.focusedItem.title} · ${props.focusedItem.segments.length} 个分段` : '选择一条缓存查看'}</span></div></div>
      {props.focusedItem ? <div className="table-scroll"><table><thead><tr><th className="check-cell"><input aria-label="选择全部分段" type="checkbox" checked={props.focusedItem.segments.length > 0 && props.selectedSegmentIds.size === props.focusedItem.segments.length} onChange={(event) => props.setSelectedSegmentIds(event.target.checked ? new Set(props.focusedItem!.segments.map((item) => item.id)) : new Set())} /></th><th>Page</th><th>分段名</th><th>结构</th><th>类型</th><th>大小</th><th>时长</th><th>可播放</th></tr></thead><tbody>{props.focusedItem.segments.map((segment) => <SegmentRow key={segment.id} item={segment} checked={props.selectedSegmentIds.has(segment.id)} toggle={() => props.setSelectedSegmentIds(toggleSet(props.selectedSegmentIds, segment.id))} play={() => { if (props.busy) return; props.setSelectedSegmentIds(new Set([segment.id])); void props.play([{ avid: props.focusedItem!.avid, pageIndexes: [segment.pageIndex] }]); }} />)}</tbody></table></div> : <Empty compact icon="film" title="没有选择缓存" body="单击上方缓存后可查看所有页面和媒体结构；双击分段可直接播放。" />}
    </section>
  </div>;
}

function SegmentRow({ item, checked, toggle, play }: { item: CacheSegment; checked: boolean; toggle(): void; play(): void }) {
  return <tr onDoubleClick={play}><td className="check-cell"><input aria-label={`选择分段 ${item.partName}`} type="checkbox" checked={checked} onChange={toggle} /></td><td>{item.pageIndex}</td><td><strong>{item.partName || item.segmentKey}</strong><small>{item.segmentKey}</small></td><td>{item.structureKind}</td><td>{item.materialKind}</td><td>{formatBytes(item.sizeBytes)}</td><td>{formatDuration(item.durationSeconds)}</td><td><span className={item.isPlayable ? 'dot good' : 'dot'} />{item.isPlayable ? '可播放' : '不可用'}</td></tr>;
}

function StoragePage({ storage, settings, refresh, cleanup, clear, open, busy }: { storage: StorageSnapshot; settings: AppSettings; refresh(): Promise<void>; cleanup(): Promise<void>; clear(): void; open(): Promise<void>; busy: boolean }) {
  const max = Math.max(storage.originalCache.bytes, storage.transcodeCache.bytes, storage.trash.bytes, 1);
  return <div className="stack"><section className="metric-grid"><Metric label="原始缓存" value={formatBytes(storage.originalCache.bytes)} detail={`${storage.originalCache.itemCount} 项`} color="blue" /><Metric label="转码缓存" value={formatBytes(storage.transcodeCache.bytes)} detail={`${storage.transcodeCache.itemCount} 项`} color="violet" /><Metric label="应用回收站" value={formatBytes(storage.trash.bytes)} detail={`${storage.trash.itemCount} 项`} color="amber" /><Metric label="合计占用" value={formatBytes(storage.totalBytes)} detail="由应用管理" color="green" /></section>
    <section className="card storage-chart"><div className="panel-heading"><div><h2>空间分布</h2><span>{storage.lastMaintenanceSummary ?? '最近没有自动维护记录'}</span></div><button className="button secondary" onClick={() => void refresh()} disabled={busy}><Icon name="refresh" />刷新统计</button></div>
      {[['B 站原始缓存', storage.originalCache.bytes, 'blue'], ['转码缓存', storage.transcodeCache.bytes, 'violet'], ['应用回收站', storage.trash.bytes, 'amber']].map(([label, bytes, color]) => <div className="bar-row" key={label as string}><div><span>{label}</span><b>{formatBytes(bytes as number)}</b></div><div className="bar-track"><i className={color as string} style={{ width: `${Math.max(2, (bytes as number) / max * 100)}%` }} /></div></div>)}
    </section><section className="card policy-card"><div><h2>转码缓存策略</h2><p>超过 {settings.transcodeCacheRetentionDays} 天或总量超过 {settings.transcodeCacheMaxSizeGigabytes} GB 时进行维护。可在“设置”中调整。</p></div><div className="toolbar"><button className="button ghost" onClick={() => void open()} disabled={busy}><Icon name="folder" />打开转码缓存目录</button><button className="button secondary" onClick={() => void cleanup()} disabled={busy}><Icon name="refresh" />按策略清理</button><button className="button danger" onClick={clear} disabled={busy}><Icon name="delete" />清空转码缓存</button></div></section></div>;
}

function TrashPage({ entries, selected, setSelected, restore, purge, busy, canPurge }: { entries: TrashEntry[]; selected: Set<string>; setSelected(value: Set<string>): void; restore(): void; purge(all: boolean): void; busy: boolean; canPurge: boolean }) {
  return <section className="card full-panel"><div className="panel-heading"><div><h2>应用回收站</h2><span>{entries.length} 项 · {formatBytes(entries.reduce((sum, item) => sum + item.sizeBytes, 0))}{!canPurge ? ' · 当前平台暂不支持永久清理' : ''}</span></div><div className="toolbar"><button className="button secondary" onClick={restore} disabled={!selected.size || busy}><Icon name="restore" />恢复所选</button>{canPurge && <button className="button danger" onClick={() => purge(true)} disabled={!entries.length || busy}>清空回收站</button>}</div></div>
    {entries.length ? <div className="table-scroll"><table><thead><tr><th className="check-cell"><input aria-label="选择全部回收站条目" type="checkbox" checked={selected.size === entries.length} onChange={(event) => setSelected(event.target.checked ? new Set(entries.map((item) => item.id)) : new Set())} /></th><th>标题</th><th>AV 号</th><th>大小</th><th>删除时间</th><th>原位置</th></tr></thead><tbody>{entries.map((item) => <tr key={item.id}><td className="check-cell"><input aria-label={`选择 ${item.title}`} type="checkbox" checked={selected.has(item.id)} onChange={() => setSelected(toggleSet(selected, item.id))} /></td><td><strong>{item.title || '未命名缓存'}</strong></td><td>av{item.avid}</td><td>{formatBytes(item.sizeBytes)}</td><td>{formatDate(item.deletedAt)}</td><td className="path-cell" title={item.originalPath}>{item.originalPath ?? '—'}</td></tr>)}</tbody></table></div> : <Empty icon="trash" title="回收站为空" body="从缓存库删除的项目会先移到这里，避免误删。" />}
  </section>;
}

function SettingsPage({ value, setValue, browse, save, busy }: { value: AppSettings; setValue(value: AppSettings | ((current: AppSettings) => AppSettings)): void; browse(): Promise<void>; save(): Promise<void>; busy: boolean }) {
  const change = <K extends keyof AppSettings>(key: K, next: AppSettings[K]) => setValue((current) => ({ ...current, [key]: next }));
  return <div className="settings-layout"><section className="card settings-section"><div className="section-title"><h2>缓存与扫描</h2><p>设置默认目录和扫描行为。</p></div><div className="settings-fields"><label>缓存根目录<div className="input-action"><input value={value.rootPath} onChange={(event) => change('rootPath', event.target.value)} /><button className="icon-button" onClick={() => void browse()} aria-label="选择缓存根目录" disabled={busy}><Icon name="folder" /></button></div></label><Check label="扫描时包含下载未完成的缓存" checked={value.includeIncomplete} onChange={(next) => change('includeIncomplete', next)} /></div></section>
    <section className="card settings-section"><div className="section-title"><h2>播放与导出</h2><p>选择外部播放器；导出由 FFmpeg 后端完成。</p></div><div className="settings-fields"><label>首选播放器<select value={value.playerPreference} onChange={(event) => change('playerPreference', event.target.value as PlayerPreference)}><option value="system">系统默认</option><option value="mpv">mpv</option><option value="vlc">VLC</option></select></label></div></section>
    <section className="card settings-section"><div className="section-title"><h2>转码缓存维护</h2><p>限制临时播放与导出产物的保留周期和总量。</p></div><div className="settings-fields inline-fields"><label>保留天数<input type="number" min="1" max="1825" value={value.transcodeCacheRetentionDays} onChange={(event) => change('transcodeCacheRetentionDays', Number(event.target.value))} /><small>1–1825 天</small></label><label>容量上限<input type="number" min="1" max="128" value={value.transcodeCacheMaxSizeGigabytes} onChange={(event) => change('transcodeCacheMaxSizeGigabytes', Number(event.target.value))} /><small>1–128 GB</small></label></div></section>
    <div className="settings-actions"><button className="button primary" disabled={busy} onClick={() => void save()}>保存设置</button></div></div>;
}

function DiagnosticsPage({ health, desktop, activities, refresh, exportReport, busy }: { health: HostHealth | null; desktop: DesktopInfo | null; activities: Activity[]; refresh(): Promise<void>; exportReport(): Promise<void>; busy: boolean }) {
  return <div className="diagnostics-layout"><section className="card health-card"><div className={`health-orb ${health?.status === 'ok' ? 'ok' : ''}`}><Icon name={health?.status === 'ok' ? 'check' : 'warning'} /></div><div><h2>{health?.status === 'ok' ? '运行环境正常' : '需要检查运行环境'}</h2><p>{health?.warnings?.join('；') || '桌面壳与 .NET Desktop Host 之间的通信状态。'}</p></div><div className="toolbar"><button className="button secondary" onClick={() => void refresh()} disabled={busy}><Icon name="refresh" />重新检查</button><button className="button primary" onClick={() => void exportReport()} disabled={busy}><Icon name="export" />导出诊断</button></div></section>
    <section className="diagnostic-grid"><div className="card"><h3>桌面运行时</h3><Description rows={[['应用版本', desktop?.appVersion], ['Electron', desktop?.electronVersion], ['Chromium', desktop?.chromiumVersion], ['Node.js', desktop?.nodeVersion], ['平台', desktop ? `${desktop.platform}/${desktop.arch}` : undefined], ['显示后端', desktop?.displayBackend]]} /></div><div className="card"><h3>.NET Host</h3><Description rows={[['状态', health?.status], ['版本', health?.version], ['.NET', health?.runtime], ['平台', health?.platform], ['FFmpeg', health?.ffmpeg]]} /></div></section>
    <section className="card activity-card"><div className="panel-heading"><div><h2>本次运行记录</h2><span>仅保留当前会话最近 50 条</span></div></div>{activities.length ? <ul>{activities.map((item, index) => <li key={`${item.time.getTime()}-${index}`}><span className={`activity-dot ${item.kind}`} /><time>{item.time.toLocaleTimeString()}</time><p>{item.message}</p></li>)}</ul> : <Empty compact icon="diagnostics" title="暂无运行记录" body="扫描、播放、导出和维护结果会显示在这里。" />}</section>
  </div>;
}

function Metric({ label, value, detail, color }: { label: string; value: string; detail: string; color: string }) { return <div className={`metric-card ${color}`}><span>{label}</span><strong>{value}</strong><small>{detail}</small></div>; }
function Check({ label, checked, onChange }: { label: string; checked: boolean; onChange(value: boolean): void }) { return <label className="check"><input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} /><span>{label}</span></label>; }
function Empty({ icon, title, body, compact = false }: { icon: IconName; title: string; body: string; compact?: boolean }) { return <div className={compact ? 'empty compact' : 'empty'}><Icon name={icon} /><h3>{title}</h3><p>{body}</p></div>; }
function Modal({ title, onClose, children }: { title: string; onClose(): void; children: ReactNode }) { return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}><div className="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title"><div className="modal-header"><h2 id="modal-title">{title}</h2><button className="icon-button" aria-label="关闭" onClick={onClose}>×</button></div>{children}</div></div>; }
function Description({ rows }: { rows: Array<[string, string | undefined]> }) { return <dl>{rows.map(([key, value]) => <div key={key}><dt>{key}</dt><dd>{value || '—'}</dd></div>)}</dl>; }

function toggleSet(current: Set<string>, value: string): Set<string> { const next = new Set(current); if (next.has(value)) next.delete(value); else next.add(value); return next; }
function selectedBytes(items: CacheEntry[], selected: Set<string>): number { return items.reduce((sum, item) => sum + (selected.has(item.id) ? item.sizeBytes : 0), 0); }
function clamp(value: number, min: number, max: number): number { return Math.max(min, Math.min(max, value)); }
function safeName(value: string): string { return value.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').slice(0, 80) || '缓存导出'; }
function dateStamp(): string { const value = new Date(); return `${value.getFullYear()}${String(value.getMonth() + 1).padStart(2, '0')}${String(value.getDate()).padStart(2, '0')}-${String(value.getHours()).padStart(2, '0')}${String(value.getMinutes()).padStart(2, '0')}`; }
function formatBytes(bytes: number): string { if (!Number.isFinite(bytes) || bytes <= 0) return '0 MB'; if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`; return `${(bytes / 1024 ** 2).toFixed(1)} MB`; }
function formatDuration(seconds: number): string { if (!Number.isFinite(seconds) || seconds <= 0) return '—'; const total = Math.round(seconds); const h = Math.floor(total / 3600); const m = Math.floor((total % 3600) / 60); const s = total % 60; return h ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`; }
function formatDate(value: string | null): string { if (!value) return '未知'; const date = new Date(value); return Number.isNaN(date.getTime()) ? value : date.toLocaleString([], { dateStyle: 'short', timeStyle: 'short' }); }
function trashTime(entry: TrashEntry): number { if (!entry.deletedAt) return 0; const value = new Date(entry.deletedAt).getTime(); return Number.isNaN(value) ? 0 : value; }
function artifactCleanupMessage(prefix: string, result: ArtifactCleanupResult): string { return `${prefix}：删除 ${result.deletedFileCount} 个文件，释放 ${formatBytes(result.freedBytes)}，失败 ${result.failedFileCount} 个，剩余 ${formatBytes(result.remainingBytes)}。`; }
function describeError(error: unknown): string { if (error instanceof Error) return error.message; if (typeof error === 'string') return error; return '操作失败，请导出诊断报告查看详情。'; }
function pageSubtitle(page: Page): string { return ({ library: '扫描、查找和管理本地 B 站缓存', storage: '了解原始缓存、转码产物与回收站占用', trash: '恢复误删条目或安全地永久清理', settings: '调整扫描、播放和缓存维护偏好', diagnostics: '检查桌面运行时、媒体工具链和最近操作' })[page]; }
function isEditable(target: EventTarget | null): boolean { return target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement || (target instanceof HTMLElement && target.isContentEditable); }
