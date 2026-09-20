import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { AppSettings, CacheDetails, CacheEntry, DesktopCapabilities, DesktopInfo, HostHealth, OperationState, PlayerPreference, SearchRequest, SelectionTarget, StorageSnapshot, TrashEntry, TrashPage as TrashPageResult, ScanResult, MediaFailure } from '../shared/contracts';
import { DEFAULT_CACHE_PAGE_SIZE, defaultSettings, emptyStorage } from '../shared/contracts';
import { Icon, type IconName } from './components/Icon';
import { buildPlaybackRetryTargets } from './playback-retry-targets';
import { cacheManager } from './cache-manager';
import { errorCode } from '../shared/ipc-result';
import { OperationProgress } from './components/OperationProgress';
import { FailureList } from './components/FailureList';
import { useShortcuts } from './hooks/useShortcuts';
import type { Page, Notice, Activity, CachePageState } from './ui-types';
import { selectedBytes, safeName, dateStamp, formatBytes, trashTime, diskOutcomeKind, diskOutcomeMessage, artifactCleanupMessage } from './display';
import { Modal } from './components/Common';
import { LibraryPage } from './pages/LibraryPage';
import { StoragePage } from './pages/StoragePage';
import { TrashPage } from './pages/TrashPage';
import { SettingsPage } from './pages/SettingsPage';
import { DiagnosticsPage } from './pages/DiagnosticsPage';

type UndoDeleteBatch = { rootPath: string; avids: string[] };
type RootBoundState<T> = { rootPath: string | null; value: T };
type LegacySettingsMigration = { rootPath: string };
type IndexBinding = { rootPath: string; includeIncomplete: boolean; indexToken: string };
type QueuedSearch = { revision: number; request: SearchRequest };
type BootstrapStatus = 'loading' | 'ready' | 'failed';
type StartupScanStatus = 'not-required' | 'running' | 'completed' | 'failed' | 'cancelled';
type BatchReport = {
  kind: 'play' | 'export'; rootPath: string; targets: SelectionTarget[];
  includeIncomplete: boolean; playerPreference: PlayerPreference;
  status: 'success' | 'partial' | 'failed' | 'cancelled' | 'unknown'; succeeded: number; failures: MediaFailure[];
  retryUnavailable?: string;
};

const initialCachePage: CachePageState = {
  offset: 0,
  pageSize: DEFAULT_CACHE_PAGE_SIZE,
  totalItems: 0,
  hasMore: false,
};

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
  const [cachePage, setCachePage] = useState<CachePageState>(initialCachePage);
  const [indexBinding, setIndexBinding] = useState<IndexBinding | null>(null);
  const [scanReport, setScanReport] = useState<ScanResult | null>(null);
  const [batchReport, setBatchReport] = useState<BatchReport | null>(null);
  const [focusedDetails, setFocusedDetails] = useState<CacheDetails | null>(null);
  const [detailsLoading, setDetailsLoading] = useState(false);
  const [detailsOffset, setDetailsOffset] = useState(0);
  const [storageState, setStorageState] = useState<RootBoundState<StorageSnapshot>>({ rootPath: null, value: emptyStorage });
  const [trashState, setTrashState] = useState<RootBoundState<TrashEntry[]>>({ rootPath: null, value: [] });
  const [trashSnapshot, setTrashSnapshot] = useState<TrashPageResult | null>(null);
  const [health, setHealth] = useState<HostHealth | null>(null);
  const [capabilities, setCapabilities] = useState<DesktopCapabilities>({ playback: true, exportMedia: true, cacheDetails: true, trashPurge: false, nativeWayland: false });
  const [desktop, setDesktop] = useState<DesktopInfo | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [focusedId, setFocusedId] = useState<string | null>(null);
  const [selectedSegmentIds, setSelectedSegmentIds] = useState<Set<string>>(new Set());
  const [selectedTrashIds, setSelectedTrashIds] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState<string | null>('正在连接 Desktop Host…');
  const [inspectionBusy, setInspectionBusy] = useState<string | null>(null);
  const inspectionInFlight = useRef(false);
  const [searching, setSearching] = useState(false);
  const [operationStates, setOperationStates] = useState<Record<string, OperationState>>({});
  const operationStatesRef = useRef<Record<string, OperationState>>({});
  const blockedOperations = useRef(false);
  const unresolved = Object.values(operationStates);
  const operationsBlocked = unresolved.some(state => state.sideEffects);
  const cancelling = unresolved.some(state => state.state === 'cancelling' || state.state === 'unconfirmed');
  const [initialized, setInitialized] = useState(false);
  const [bootstrapStatus, setBootstrapStatus] = useState<BootstrapStatus>('loading');
  const [settingsLoaded, setSettingsLoaded] = useState(false);
  const [startupScanStatus, setStartupScanStatus] = useState<StartupScanStatus>('not-required');
  const [startupScanCount, setStartupScanCount] = useState(0);
  const [bootstrapError, setBootstrapError] = useState('');
  const [progressRevision, resetProgress] = useState(0);
  const [notices, setNotices] = useState<Notice[]>([]);
  const [activities, setActivities] = useState<Activity[]>([]);
  const [confirm, setConfirm] = useState<{ title: string; body: string; destructive?: boolean; action(): void } | null>(null);
  const [legacySettingsMigration, setLegacySettingsMigration] = useState<LegacySettingsMigration | null>(null);
  const [undoDeleteBatch, setUndoDeleteBatch] = useState<UndoDeleteBatch | null>(null);
  const noticeId = useRef(0);
  const searchInput = useRef<HTMLInputElement>(null);
  const searchWasActive = useRef(false);
  const pendingSearch = useRef<QueuedSearch | null>(null);
  const latestSearchRevision = useRef(0);
  const searchDrainActive = useRef(false);
  const resumePendingSearch = useRef<() => void>(() => undefined);
  const operationInFlight = useRef(true);
  const detailsRevision = useRef(0);
  const activeRootPath = settings.rootPath.trim();
  const activeRootPathRef = useRef(activeRootPath);
  useLayoutEffect(() => { activeRootPathRef.current = activeRootPath; }, [activeRootPath]);
  const storage = storageState.rootPath === activeRootPath ? storageState.value : emptyStorage;
  const trash = trashState.rootPath === activeRootPath ? trashState.value : [];
  const hasActiveIndex = indexBinding?.rootPath === activeRootPath &&
    indexBinding.includeIncomplete === settings.includeIncomplete &&
    Boolean(indexBinding.indexToken);
  const currentSearchRequest: SearchRequest = {
    indexToken: indexBinding?.indexToken ?? '',
    offset: 0,
    pageSize: cachePage.pageSize,
    keyword: settings.keyword.trim(),
    matchMode: settings.matchMode,
    splitKeywords: settings.splitKeywords,
    anyKeywords: settings.anyKeywords,
    includePartName: settings.includePartName,
    includeOwnerName: settings.includeOwnerName,
    includeBvid: settings.includeBvid,
    includeAvid: settings.includeAvid,
    caseSensitive: settings.caseSensitive,
  };
  const searchContextRef = useRef({ hasActiveIndex, request: currentSearchRequest });
  useLayoutEffect(() => { searchContextRef.current = { hasActiveIndex, request: currentSearchRequest }; });

  const notify = useCallback((kind: Notice['kind'], message: string) => {
    const id = ++noticeId.current;
    setNotices((current) => [...current.slice(-3), { id, kind, message }]);
    setActivities((current) => [{ time: new Date(), kind, message }, ...current].slice(0, 50));
    window.setTimeout(() => setNotices((current) => current.filter((item) => item.id !== id)), 4_500);
  }, []);

  const replaceLibraryItems = useCallback((nextItems: CacheEntry[] = [], page?: CachePageState) => {
    void cacheManager.cancelCacheDetails().catch(() => undefined);
    setItems(nextItems);
    setCachePage(page ?? { ...initialCachePage, totalItems: nextItems.length });
    setSelectedIds(new Set());
    setFocusedId(null);
    setFocusedDetails(null);
    setDetailsOffset(0);
    detailsRevision.current += 1;
    setDetailsLoading(false);
    setSelectedSegmentIds(new Set());
  }, []);

  const invalidateIndex = useCallback(() => {
    void cacheManager.cancelSearch().catch(() => undefined);
    searchDrainActive.current = false;
    setSearching(false);
    setScanReport(null);
    latestSearchRevision.current += 1;
    pendingSearch.current = null;
    searchWasActive.current = false;
    setIndexBinding(null);
    replaceLibraryItems();
  }, [replaceLibraryItems]);

  const clearLibrary = useCallback(() => replaceLibraryItems(), [replaceLibraryItems]);

  const invalidateRootViews = useCallback(() => {
    setTrashSnapshot(null);
    setStorageState({ rootPath: null, value: emptyStorage });
    setTrashState({ rootPath: null, value: [] });
    setSelectedTrashIds(new Set());
  }, []);

  const invalidateStorage = useCallback(() => {
    setStorageState({ rootPath: null, value: emptyStorage });
  }, []);

  const bindStorage = useCallback((rootPath: string, value: StorageSnapshot) => {
    if (activeRootPathRef.current === rootPath) setStorageState({ rootPath, value });
  }, []);

  const bindTrash = useCallback((rootPath: string, value: TrashPageResult) => {
    if (activeRootPathRef.current === rootPath) {
      setTrashState({ rootPath, value: value.items });
      setTrashSnapshot(value);
    }
  }, []);

  const run = useCallback(async <T,>(label: string, operation: () => Promise<T>): Promise<T | undefined> => {
    if (blockedOperations.current) { notify('info', '上次操作结果尚未确认，请先核对结果。'); return undefined; }
    if (operationInFlight.current || inspectionInFlight.current) return undefined;
    operationInFlight.current = true;
    setBusy(label);
    try {
      return await operation();
    } catch (error) {
      if (isStaleIndexError(error)) {
        invalidateIndex();
        notify('info', '缓存索引已失效，请重新扫描。');
      } else if (errorCode(error) === 'stale_trash') {
        setTrashSnapshot(null);
        notify('info', '回收站快照已失效，请刷新后重试。');
      } else {
        notify(isCancellationError(error) ? 'info' : 'error', isCancellationError(error) ? '操作已取消。' : describeError(error));
      }
      return undefined;
    } finally {
      operationInFlight.current = false;
      setBusy(null);
      resetProgress(value => value + 1);
      resumePendingSearch.current();
    }
  }, [invalidateIndex, notify]);

  const inspect = useCallback(async <T,>(label: string, operation: () => Promise<T>): Promise<T | undefined> => {
    if (inspectionInFlight.current || (operationInFlight.current && !blockedOperations.current)) return undefined;
    inspectionInFlight.current = true;
    setInspectionBusy(label);
    try {
      return await operation();
    } catch (error) {
      if (errorCode(error) === 'stale_trash') setTrashSnapshot(null);
      notify(isCancellationError(error) ? 'info' : 'error', isCancellationError(error) ? '核对操作已取消。' : describeError(error));
      return undefined;
    } finally {
      inspectionInFlight.current = false;
      setInspectionBusy(null);
    }
  }, [notify]);

  const refreshAfterMutation = useCallback(async <T,>(operation: () => Promise<T>): Promise<T | null> => {
    try { return await operation(); }
    catch (error) {
      notify('info', '文件操作结果已保留，但刷新失败，请稍后重新核对：' + describeError(error));
      return null;
    }
  }, [notify]);

  const reportScan = useCallback((result: ScanResult, prefix = '') => {
    setScanReport(result);
    const errors = (result.invalidEntries ?? 0) + (result.inaccessibleDirectories ?? 0);
    notify(errors > 0 ? 'error' : result.hasWarnings ? 'info' : 'success',
      `${prefix}扫描完成，共发现 ${result.totalItems} 条缓存；损坏 ${result.invalidEntries ?? 0} 条，无法访问 ${result.inaccessibleDirectories ?? 0} 处，跳过未完成 ${result.skippedIncompleteEntries ?? 0} 条。`);
  }, [notify]);

  const applyScanResult = useCallback((rootPath: string, includeIncomplete: boolean, result: ScanResult, prefix = '') => {
    setIndexBinding({ rootPath, includeIncomplete, indexToken: result.indexToken });
    replaceLibraryItems(result.items, result);
    invalidateStorage();
    reportScan(result, prefix);
  }, [invalidateStorage, replaceLibraryItems, reportScan]);

  const restoreAndRescan = useCallback(async (rootPath: string, entryIds: string[], includeIncomplete: boolean) => {
    const result = entryIds.length ? await cacheManager.restoreTrash(rootPath, entryIds)
      : { restored: [], failed: [], cancelled: false, unprocessed: [] };
    if (result.restored.length > 0) invalidateIndex();
    const scanResult = result.restored.length && !result.cancelled
      ? await refreshAfterMutation(() => cacheManager.scan({ rootPath, includeIncomplete, persistSettings: false, offset: 0, pageSize: DEFAULT_CACHE_PAGE_SIZE }))
      : null;
    return { result, scanResult };
  }, [invalidateIndex, refreshAfterMutation]);

  useEffect(() => {
    let disposed = false;
    let restoringStates = true;
    const stateUpdates = new Map<string, OperationState>();
    const unsubscribeState = cacheManager.onOperationState((value) => {
      if (disposed) return;
      if (value.operation === 'search' || value.operation === 'cache.details') return;
      if (restoringStates) stateUpdates.set(value.requestId, value);
      const next = { ...operationStatesRef.current };
      if (value.state === 'settled') delete next[value.requestId];
      else next[value.requestId] = value;
      operationStatesRef.current = next;
      blockedOperations.current = Object.values(next).some(state => state.sideEffects);
      setOperationStates(next);
    });
    const unsubscribeUnavailable = cacheManager.onHostUnavailable((message) => {
      setHealth(null);
      invalidateIndex();
      notify('error', message);
    });
    void (async () => {
      try {
        const snapshot = await cacheManager.getOperationStates();
        if (disposed) return;
        // Events received while the snapshot was in flight take precedence,
        // including settled events that must not resurrect an old restriction.
        const restored: Record<string, OperationState> = {};
        for (const value of [...snapshot, ...stateUpdates.values()]) {
          if (value.operation === 'search' || value.operation === 'cache.details') continue;
          if (value.state === 'settled') delete restored[value.requestId];
          else restored[value.requestId] = value;
        }
        restoringStates = false;
        stateUpdates.clear();
        blockedOperations.current = Object.values(restored).some(state => state.sideEffects);
        operationStatesRef.current = restored;
        setOperationStates(restored);
        const [initial, info] = await Promise.all([
          cacheManager.getInitialState(),
          cacheManager.getDesktopInfo(),
        ]);
        if (disposed) return;
        // Health is advisory and starts after the Host has completed cold startup.
        void cacheManager.health().then(value => { if (!disposed) setHealth(value); })
          .catch(error => { if (!disposed) notify('info', `运行环境检查暂不可用：${describeError(error)}`); });
        const loadedSettings = initial.settings;
        setSettings(loadedSettings);
        setDraftSettings(loadedSettings);
        setSettingsLoaded(true);
        replaceLibraryItems(initial.items);
        setIndexBinding(null);
        setStorageState({ rootPath: null, value: emptyStorage });
        setTrashState({ rootPath: null, value: [] });
        setCapabilities(initial.capabilities);
        setDesktop(info);
        notify('success', 'Desktop Host 已连接。');

        const legacyRootPath = loadedSettings.rootPath.trim();
        const requiresLegacyChoice = legacyRootPath.length > 0
          && typeof initial.settingsState.sourceSchemaVersion === 'number'
          && initial.settingsState.sourceSchemaVersion < 2;
        if (requiresLegacyChoice) {
          replaceLibraryItems();
          setLegacySettingsMigration({ rootPath: legacyRootPath });
        } else if (loadedSettings.scanOnStartup && legacyRootPath) {
          setStartupScanStatus('running');
          setBusy('正在自动扫描缓存…');
          try {
            const result = await cacheManager.scan({
              rootPath: legacyRootPath,
              includeIncomplete: loadedSettings.includeIncomplete,
              persistSettings: false,
              offset: 0,
              pageSize: DEFAULT_CACHE_PAGE_SIZE,
            });
            if (disposed) return;
            applyScanResult(legacyRootPath, loadedSettings.includeIncomplete, result);
            setStartupScanCount(result.totalItems);
            setStartupScanStatus('completed');
          } catch (error) {
            if (disposed) return;
            invalidateIndex();
            const cancelled = isCancellationError(error);
            setStartupScanStatus(cancelled ? 'cancelled' : 'failed');
            notify(cancelled ? 'info' : 'error', cancelled ? '启动扫描已取消。' : `启动扫描失败：${describeError(error)}`);
          }
        }
        if (disposed) return;
        setBootstrapStatus('ready');
      } catch (error) {
        if (disposed) return;
        const message = isCancellationError(error) ? '初始化已取消。' : describeError(error);
        setStartupScanStatus((current) => current === 'running' ? 'failed' : current);
        setBootstrapError(safeBootstrapError(message));
        setBootstrapStatus('failed');
        notify(isCancellationError(error) ? 'info' : 'error', message);
      } finally {
        if (disposed) return;
        operationInFlight.current = false;
        setBusy(null);
        resetProgress(value => value + 1);
        setInitialized(true);
        resumePendingSearch.current();
      }
    })();
    return () => {
      disposed = true; unsubscribeUnavailable(); unsubscribeState();
      void cacheManager.cancelSearch().catch(() => undefined);
    };
  }, [applyScanResult, invalidateIndex, notify, replaceLibraryItems]);

  const focusedItem = useMemo(
    () => items.find((item) => item.id === focusedId || item.avid === focusedId) ?? null,
    [focusedId, items],
  );
  useEffect(() => {
    if (!focusedItem || !indexBinding?.indexToken) {
      setFocusedDetails(null);
      setDetailsLoading(false);
      return;
    }
    const revision = ++detailsRevision.current;
    setFocusedDetails(null);
    setSelectedSegmentIds(new Set());
    setDetailsLoading(true);
    void (async () => {
      try {
        const result = await cacheManager.getCacheDetails({
          indexToken: indexBinding.indexToken,
          avid: focusedItem.avid,
          offset: detailsOffset,
          pageSize: DEFAULT_CACHE_PAGE_SIZE,
        });
        if (detailsRevision.current !== revision) return;
        setFocusedDetails(result);
      } catch (error) {
        if (detailsRevision.current !== revision) return;
        if (isStaleIndexError(error)) {
          invalidateIndex();
          notify('info', '缓存索引已失效，请重新扫描。');
        } else {
          notify(isCancellationError(error) ? 'info' : 'error', isCancellationError(error) ? '分段加载已取消。' : describeError(error));
        }
      } finally {
        if (detailsRevision.current === revision) setDetailsLoading(false);
      }
    })();
    return () => {
      detailsRevision.current += 1;
      void cacheManager.cancelCacheDetails().catch(() => undefined);
    };
  }, [detailsOffset, focusedItem, indexBinding?.indexToken, invalidateIndex, notify]);

  const targets = useMemo<SelectionTarget[]>(() => {
    if (focusedDetails && selectedSegmentIds.size > 0) {
      return [{
        avid: focusedDetails.avid,
        pageIndexes: focusedDetails.segments.filter((segment) => selectedSegmentIds.has(segment.id)).map((segment) => segment.pageIndex),
      }];
    }
    return items.filter((item) => selectedIds.has(item.id)).map((item) => ({ avid: item.avid }));
  }, [focusedDetails, items, selectedIds, selectedSegmentIds]);

  const browse = useCallback(async () => {
    const completed = await run('正在验证缓存目录…', async () => {
      const rootPath = await cacheManager.chooseRootDirectory(settings.rootPath);
      if (!rootPath) return null;
      const normalizedRootPath = rootPath.trim();
      const result = await cacheManager.scan({
        rootPath: normalizedRootPath,
        includeIncomplete: settings.includeIncomplete,
        persistSettings: false,
        offset: 0,
        pageSize: DEFAULT_CACHE_PAGE_SIZE,
      });
      invalidateIndex();
      const saved = await cacheManager.updateSettings({
        rootPath: normalizedRootPath,
        includeIncomplete: settings.includeIncomplete,
      });
      const sessionSettings = { ...saved, rootPath: normalizedRootPath };
      return { saved: sessionSettings, result, rootPath: normalizedRootPath };
    });
    if (!completed) return;
    setSettings(completed.saved);
    setDraftSettings(completed.saved);
    setUndoDeleteBatch(null);
    invalidateRootViews();
    applyScanResult(completed.rootPath, settings.includeIncomplete, completed.result);
  }, [applyScanResult, restoreAndRescan, invalidateIndex, invalidateRootViews, notify, replaceLibraryItems, reportScan, run, settings.includeIncomplete, settings.rootPath]);

  const scan = useCallback(async () => {
    if (!settings.rootPath.trim()) { notify('error', '请先选择 B 站缓存根目录。'); return; }
    const result = await run('正在扫描缓存…', async () => {
      const next = await cacheManager.scan({
        rootPath: activeRootPath,
        includeIncomplete: settings.includeIncomplete,
        persistSettings: true,
        offset: 0,
        pageSize: DEFAULT_CACHE_PAGE_SIZE,
      });
      invalidateIndex();
      return next;
    });
    if (!result) return;
    applyScanResult(activeRootPath, settings.includeIncomplete, result);
  }, [applyScanResult, restoreAndRescan, activeRootPath, invalidateIndex, invalidateStorage, notify, replaceLibraryItems, reportScan, run, settings.includeIncomplete]);

  const drainSearchQueue = useCallback(async () => {
    if (operationInFlight.current || !pendingSearch.current) return;
    const queued = pendingSearch.current;
    pendingSearch.current = null;
    searchDrainActive.current = true;
    setSearching(true);
    try {
      const result = await cacheManager.search(queued.request);
      const context = searchContextRef.current;
      if (queued.revision === latestSearchRevision.current && context.hasActiveIndex &&
          sameSearchRequest(context.request, queued.request)) replaceLibraryItems(result.items, result);
    } catch (error) {
      if (queued.revision !== latestSearchRevision.current || !searchContextRef.current.hasActiveIndex ||
          !sameSearchRequest(searchContextRef.current.request, queued.request)) return;
      if (isStaleIndexError(error)) {
        invalidateIndex();
        notify('info', '缓存索引已失效，请重新扫描。');
      } else if (!isCancellationError(error)) notify('error', describeError(error));
    } finally {
      if (queued.revision === latestSearchRevision.current) {
        searchDrainActive.current = false;
        setSearching(false);
      }
    }
  }, [invalidateIndex, notify, replaceLibraryItems]);
  useLayoutEffect(() => { resumePendingSearch.current = () => { void drainSearchQueue(); }; }, [drainSearchQueue]);

  const search = useCallback(async (offset = 0) => {
    if (!hasActiveIndex) {
      notify('info', '请先扫描当前缓存目录，再进行搜索。');
      return;
    }
    const request = { ...searchContextRef.current.request, offset };
    const revision = ++latestSearchRevision.current;
    pendingSearch.current = { revision, request };
    resumePendingSearch.current();
  }, [hasActiveIndex, notify]);

  useEffect(() => {
    if (!initialized || legacySettingsMigration || !hasActiveIndex) return;
    const hasKeyword = Boolean(settings.keyword.trim());
    if (!hasKeyword && !searchWasActive.current) return;
    const timer = window.setTimeout(() => {
      searchWasActive.current = hasKeyword;
      void search();
    }, 350);
    return () => window.clearTimeout(timer);
  }, [
    hasActiveIndex,
    initialized,
    legacySettingsMigration,
    search,
    settings.anyKeywords,
    settings.caseSensitive,
    settings.includeAvid,
    settings.includeBvid,
    settings.includeOwnerName,
    settings.includePartName,
    settings.keyword,
    settings.matchMode,
    settings.splitKeywords,
  ]);

  useEffect(() => {
    if (hasActiveIndex) return;
    if (indexBinding) {
      invalidateIndex();
      return;
    }
    latestSearchRevision.current += 1;
    pendingSearch.current = null;
    searchWasActive.current = false;
  }, [activeRootPath, hasActiveIndex, indexBinding, invalidateIndex, settings.includeIncomplete]);

  const cancelCurrentOperation = useCallback(async () => {
    if (searchDrainActive.current) {
      latestSearchRevision.current += 1;
      pendingSearch.current = null;
      searchDrainActive.current = false;
      setSearching(false);
      await cacheManager.cancelSearch().catch(error => notify('error', describeError(error)));
      return;
    }
    if (detailsLoading) {
      detailsRevision.current += 1;
      setDetailsLoading(false);
    }
    try {
      await cacheManager.cancel();
    } catch (error) {
      notify('error', describeError(error));
    }
  }, [detailsLoading, notify]);

  const updateSetting = useCallback(<K extends keyof AppSettings>(key: K, value: AppSettings[K]) => {
    setSettings((current) => ({ ...current, [key]: value }));
  }, []);

  const play = useCallback(async (explicitTargets?: SelectionTarget[], retry?: BatchReport) => {
    const requestedTargets = explicitTargets ?? targets;
    if (requestedTargets.length === 0) { notify('info', '请先选择缓存或分段。'); return; }
    if (!activeRootPath) { notify('error', '当前没有有效的缓存根目录。'); return; }
    const context = { kind: 'play' as const, rootPath: retry?.rootPath ?? activeRootPath, targets: requestedTargets,
      playerPreference: retry?.playerPreference ?? settings.playerPreference, includeIncomplete: retry?.includeIncomplete ?? settings.includeIncomplete };
    setBatchReport(null);
    const result = await run('正在准备播放…', async () => {
      try { return await cacheManager.play(context.rootPath, requestedTargets, context.playerPreference, context.includeIncomplete); }
      catch (error) {
        setBatchReport({ ...context, status: isUnknownOutcome(error) ? 'unknown' : isCancellationError(error) ? 'cancelled' : 'failed', succeeded: 0,
          failures: [{ avid: '', pageIndex: null, title: '', message: describeError(error) }] });
        throw error;
      }
    });
    if (result) {
      const failures = result.failures;
      let retryTargets: SelectionTarget[] = [];
      let retryUnavailable: string | undefined;
      try { retryTargets = buildPlaybackRetryTargets(failures); }
      catch (error) { retryUnavailable = describeError(error); }
      setBatchReport({ ...context, targets: retryTargets, retryUnavailable,
        status: result.queued === 0 ? 'failed' : failures.length ? 'partial' : 'success', succeeded: result.queued, failures });
      notify(result.queued === 0 ? 'error' : failures.length ? 'info' : 'success', `已将 ${result.queued} 个页面交给播放器，失败 ${failures.length} 项。`);
    }
  }, [activeRootPath, notify, run, settings.includeIncomplete, settings.playerPreference, targets]);

  const exportMedia = useCallback(async (explicitTargets?: SelectionTarget[], retry?: BatchReport) => {
    const requestedTargets = explicitTargets ?? targets;
    if (requestedTargets.length === 0) { notify('info', '请先选择要导出的缓存或分段。'); return; }
    if (!activeRootPath) { notify('error', '当前没有有效的缓存根目录。'); return; }
    const title = targets.length === 1 && focusedItem ? safeName(focusedItem.title) : `缓存导出-${dateStamp()}`;
    const context = { kind: 'export' as const, rootPath: retry?.rootPath ?? activeRootPath, targets: requestedTargets,
      includeIncomplete: retry?.includeIncomplete ?? settings.includeIncomplete, playerPreference: settings.playerPreference };
    setBatchReport(null);
    const result = await run('正在导出 MP4…', async () => {
      try { return await cacheManager.exportMedia(context.rootPath, requestedTargets, `${title}.mp4`, context.includeIncomplete); }
      catch (error) {
        setBatchReport({ ...context, status: isUnknownOutcome(error) ? 'unknown' : isCancellationError(error) ? 'cancelled' : 'failed', succeeded: 0,
          failures: [{ avid: '', pageIndex: null, title: '', message: describeError(error) }] });
        throw error;
      }
    });
    if (result) {
      setBatchReport({ ...context, status: result.published ? 'success' : 'failed', succeeded: result.exportedCount, failures: result.failures });
      notify(result.published ? 'success' : 'error', result.published ? `已导出：${result.outputPath}` : '本批导出未发布，请查看失败明细。');
    } else if (result === null) {
      setBatchReport({ ...context, status: 'cancelled', succeeded: 0, failures: [] });
    }
  }, [activeRootPath, focusedItem, notify, run, settings.includeIncomplete, targets]);

  const moveToTrash = useCallback(() => {
    const avids = items.filter((item) => selectedIds.has(item.id)).map((item) => item.avid);
    if (avids.length === 0) { notify('info', '请先选择要删除的缓存。'); return; }
    const rootPath = activeRootPath;
    if (!rootPath) { notify('error', '当前没有有效的缓存根目录。'); return; }
    setConfirm({
      title: `移入回收站（${avids.length} 项）`,
      body: '所选缓存将移动到应用回收站，之后仍可恢复。正在播放或导出的项目请先停止操作。',
      destructive: true,
      action: () => { void (async () => {
        if (activeRootPathRef.current !== rootPath) {
          notify('error', '缓存根目录已变化，已取消移动到回收站。');
          return;
        }
        const completed = await run('正在移动到回收站…', async () => {
          const result = await cacheManager.moveToTrash(rootPath, avids);
          if (result.moved.length > 0) invalidateIndex();
          return { result };
        });
        if (!completed) return;
        if (completed.result.moved.length > 0) {
          setIndexBinding(null);
          replaceLibraryItems();
        } else {
          setSelectedIds(new Set());
          setFocusedId(null);
          setSelectedSegmentIds(new Set());
        }
        invalidateRootViews();
        setUndoDeleteBatch(completed.result.moved.length > 0
          ? { rootPath, avids: completed.result.moved }
          : null);
        notify(diskOutcomeKind(completed.result), diskOutcomeMessage('移动', completed.result.moved.length, completed.result) + (completed.result.moved.length ? '可按 Ctrl+Z 撤销。' : ''));
      })(); },
    });
  }, [activeRootPath, invalidateIndex, invalidateRootViews, items, notify, replaceLibraryItems, run, selectedIds]);

  useEffect(() => {
    invalidateRootViews();
    setUndoDeleteBatch((current) => current && current.rootPath !== activeRootPath ? null : current);
  }, [activeRootPath, invalidateRootViews]);

  const undoLastDelete = useCallback(async () => {
    const batch = undoDeleteBatch;
    if (!batch) { notify('info', '没有可撤销的删除操作。'); return; }
    if (batch.rootPath !== activeRootPath) {
      setUndoDeleteBatch(null);
      notify('info', '缓存根目录已变化，不能撤销之前目录中的删除。');
      return;
    }
    const rootPath = batch.rootPath;

    const completed = await run('正在撤销删除…', async () => {
      const entries: TrashEntry[] = [];
      let page = await cacheManager.getTrashPage(rootPath, { pageSize: 200 });
      entries.push(...page.items);
      while (page.hasMore) {
        page = await cacheManager.getTrashPage(rootPath, { snapshotToken: page.snapshotToken, offset: page.offset + page.pageSize, pageSize: 200 });
        entries.push(...page.items);
      }
      const requestedAvids = new Set(batch.avids);
      const newestByAvid = new Map<string, TrashEntry>();
      for (const entry of [...entries].sort((left, right) => trashTime(right) - trashTime(left))) {
        if (requestedAvids.has(entry.avid) && !newestByAvid.has(entry.avid)) newestByAvid.set(entry.avid, entry);
      }
      const entryIds = batch.avids.flatMap((avid) => {
        const entry = newestByAvid.get(avid);
        return entry ? [entry.id] : [];
      });
      const { result: restoreResult, scanResult } = await restoreAndRescan(rootPath, entryIds, settings.includeIncomplete);
      return {
        restoreResult,
        scanResult,
        missingCount: batch.avids.length - entryIds.length,
        remainingAvids: batch.avids.filter(avid => {
          const entry = newestByAvid.get(avid);
          return entry && !restoreResult.restored.includes(entry.id);
        }),
      };
    });
    if (!completed) return;
    setUndoDeleteBatch(completed.remainingAvids.length ? { rootPath, avids: completed.remainingAvids } : null);
    invalidateRootViews();
    if (completed.scanResult) {
      applyScanResult(rootPath, settings.includeIncomplete, completed.scanResult);
    }
    const failedCount = completed.restoreResult.failed.length + completed.missingCount;
    notify(failedCount ? 'error' : diskOutcomeKind(completed.restoreResult), diskOutcomeMessage('撤销删除', completed.restoreResult.restored.length, completed.restoreResult, failedCount));
  }, [applyScanResult, restoreAndRescan, activeRootPath, invalidateIndex, invalidateRootViews, notify, refreshAfterMutation, replaceLibraryItems, reportScan, run, settings.includeIncomplete, undoDeleteBatch]);

  const refreshStorage = useCallback(async (announce = true) => {
    const rootPath = activeRootPath;
    const value = await inspect('正在统计存储…', () => cacheManager.getStorage(rootPath || undefined));
    if (value) {
      bindStorage(rootPath, value);
      if (announce) notify('success', '存储统计已刷新。');
    }
  }, [activeRootPath, bindStorage, notify, inspect]);

  const refreshTrash = useCallback(async (announce = true, offset = 0, snapshotToken?: string) => {
    const rootPath = activeRootPath;
    if (!rootPath) {
      setTrashState({ rootPath, value: [] });
      return;
    }
    const value = await inspect('正在读取回收站…', () => cacheManager.getTrashPage(rootPath, { offset, pageSize: 100, snapshotToken }));
    if (value) {
      bindTrash(rootPath, value);
      setSelectedTrashIds(new Set());
      if (announce) notify('success', '回收站已刷新。');
    }
  }, [activeRootPath, bindTrash, notify, inspect]);

  useEffect(() => {
    if (!initialized || (busy && !operationsBlocked) || legacySettingsMigration) return;
    if (page === 'storage' && storageState.rootPath !== activeRootPath) void refreshStorage(false);
    if (page === 'trash' && trashState.rootPath !== activeRootPath) void refreshTrash(false);
  }, [activeRootPath, busy, initialized, legacySettingsMigration, operationsBlocked, page, refreshStorage, refreshTrash, storageState.rootPath, trashState.rootPath]);

  const cleanupTranscodeCache = useCallback(async () => {
    const rootPath = activeRootPath;
    const completed = await run('正在按策略清理转码缓存…', async () => {
      const result = await cacheManager.cleanupTranscodeCache();
      return { result, snapshot: result.cancelled ? null : await refreshAfterMutation(() => cacheManager.getStorage(rootPath || undefined)) };
    });
    if (!completed) return;
    if (completed.snapshot) bindStorage(rootPath, completed.snapshot);
    else invalidateStorage();
    notify(completed.result.failedFileCount ? 'error' : completed.result.cancelled ? 'info' : 'success', artifactCleanupMessage('清理完成', completed.result));
  }, [activeRootPath, bindStorage, invalidateStorage, notify, refreshAfterMutation, run]);

  const openTranscodeCache = useCallback(async () => {
    const opened = await inspect('正在打开转码缓存目录…', () => cacheManager.openTranscodeCache());
    if (opened) notify('success', '已打开受管转码缓存目录。');
  }, [notify, inspect]);

  const requestClearTranscodeCache = useCallback(() => {
    const rootPath = activeRootPath;
    setConfirm({
      title: '清空转码缓存',
      body: '将清空应用管理的转码产物，不会删除 B 站原始缓存。确认后系统还会再询问一次。',
      destructive: true,
      action: () => { void (async () => {
        const completed = await run('正在清空转码缓存…', async () => {
          const result = await cacheManager.clearTranscodeCache();
          if (!result) return null;
          return { result, snapshot: result.cancelled ? null : await refreshAfterMutation(() => cacheManager.getStorage(rootPath || undefined)) };
        });
        if (!completed) return;
        if (completed.snapshot) bindStorage(rootPath, completed.snapshot);
        else invalidateStorage();
        notify(completed.result.failedFileCount ? 'error' : completed.result.cancelled ? 'info' : 'success', artifactCleanupMessage('清空完成', completed.result));
      })(); },
    });
  }, [activeRootPath, bindStorage, invalidateStorage, notify, refreshAfterMutation, run]);

  const resolveLegacySettingsMigration = useCallback(async (choice: 'scan' | 'remember' | 'forget') => {
    const migration = legacySettingsMigration;
    if (!migration) return;
    const patch: Partial<AppSettings> = choice === 'forget'
      ? { rootPath: '', rememberRootPath: false, scanOnStartup: false }
      : { rootPath: migration.rootPath, rememberRootPath: true, scanOnStartup: choice === 'scan' };
    const saved = await run('正在保存启动扫描选择…', async () => {
      const next = await cacheManager.updateSettings(patch);
      invalidateIndex();
      return next;
    });
    if (!saved) return;

    const sessionSettings = choice === 'forget' ? saved : { ...saved, rootPath: migration.rootPath };
    setSettings(sessionSettings);
    setDraftSettings(sessionSettings);
    setLegacySettingsMigration(null);
    setUndoDeleteBatch(null);
    invalidateRootViews();
    replaceLibraryItems();
    if (choice === 'forget') {
      setIndexBinding(null);
      notify('success', '已忘记旧缓存目录，启动时不会扫描。');
      return;
    }
    if (choice === 'remember') {
      notify('success', '已记住缓存目录；启动时不会自动扫描。');
      return;
    }

    const result = await run('正在扫描旧缓存目录…', () => cacheManager.scan({
      rootPath: migration.rootPath,
      includeIncomplete: sessionSettings.includeIncomplete,
      persistSettings: false,
      offset: 0,
      pageSize: DEFAULT_CACHE_PAGE_SIZE,
    }));
    if (!result) return;
    applyScanResult(migration.rootPath, sessionSettings.includeIncomplete, result, '已启用启动扫描。');
  }, [applyScanResult, restoreAndRescan, invalidateIndex, invalidateRootViews, invalidateStorage, legacySettingsMigration, notify, replaceLibraryItems, reportScan, run]);

  useShortcuts((event) => {
    const uiBusy = Boolean(busy) || Boolean(inspectionBusy) || detailsLoading || searching || operationsBlocked;
      if (event.key === 'F5') { event.preventDefault(); if (!uiBusy) void scan(); }
      if (event.key === 'Escape' && uiBusy) { event.preventDefault(); void cancelCurrentOperation(); }
      if (event.ctrlKey && event.key.toLowerCase() === 'f') { event.preventDefault(); setPage('library'); window.setTimeout(() => searchInput.current?.focus(), 0); }
      if (event.ctrlKey && event.key.toLowerCase() === 'e') { event.preventDefault(); if (!uiBusy) void exportMedia(); }
      if (event.ctrlKey && !event.shiftKey && event.key.toLowerCase() === 'z' && !isEditable(event.target)) { event.preventDefault(); if (!uiBusy) void undoLastDelete(); }
      if (event.key === 'Delete' && page === 'library' && !isEditable(event.target) && !uiBusy) moveToTrash();
  });

  const uiBusy = Boolean(busy) || Boolean(inspectionBusy) || detailsLoading || searching || operationsBlocked;
  const focusCache = useCallback((item: CacheEntry) => {
    setDetailsOffset(0); setFocusedId(item.id); setSelectedSegmentIds(new Set());
  }, []);
  const inspectionDisabled = !initialized || Boolean(inspectionBusy) || (Boolean(busy) && !operationsBlocked);

  if (bootstrapStatus === 'failed') {
    return <div
      className="bootstrap-failure"
      data-renderer-ready="false"
      data-renderer-bootstrap="failed"
      data-settings-loaded={settingsLoaded ? 'true' : 'false'}
      data-startup-scan={startupScanStatus}
      data-startup-scan-count={startupScanCount}
      data-host-status={health?.status ?? 'unavailable'}
      data-bootstrap-error={bootstrapError}
    >
      <div className="bootstrap-failure-card" role="alert">
        <Icon name="warning" />
        <h1>桌面端初始化失败</h1>
        <p>{bootstrapError || 'Desktop Host 返回了无法使用的初始化数据。'}</p>
        <button className="button primary" onClick={() => window.location.reload()}>重试初始化</button>
      </div>
    </div>;
  }

  return (
    <div
      className="app-shell"
      data-renderer-ready={bootstrapStatus === 'ready' ? 'true' : 'false'}
      data-renderer-bootstrap={bootstrapStatus}
      data-settings-loaded={settingsLoaded ? 'true' : 'false'}
      data-startup-scan={startupScanStatus}
      data-startup-scan-count={startupScanCount}
      data-host-status={health?.status ?? 'unavailable'}
      data-bootstrap-error={bootstrapError}
    >
      <aside className="sidebar">
        <div className="brand"><div className="brand-mark"><Icon name="film" /></div><div><strong>缓存管理器</strong><span>Desktop</span></div></div>
        <nav aria-label="主导航">
          {navigation.map((item) => (
            <button key={item.id} className={page === item.id ? 'nav-item active' : 'nav-item'} onClick={() => setPage(item.id)}>
              <Icon name={item.icon} /><span>{item.label}</span>
              {item.id === 'trash' && (trashSnapshot?.totalItems ?? trash.length) > 0 && <b>{trashSnapshot?.totalItems ?? trash.length}</b>}
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
            {undoDeleteBatch && <button className="button secondary" onClick={() => void undoLastDelete()} disabled={uiBusy}><Icon name="restore" />撤销删除 <kbd>Ctrl+Z</kbd></button>}
            {(busy || inspectionBusy || searching || detailsLoading) && <button className="button ghost" disabled={cancelling} onClick={() => void cancelCurrentOperation()}><Icon name="stop" />{cancelling ? '正在取消' : '取消'}</button>}
            <button className="button primary" onClick={() => void scan()} disabled={uiBusy}><Icon name="scan" />扫描缓存 <kbd>F5</kbd></button>
          </div>
        </header>
        {unresolved.map(state => <section className="result-panel operation-state" role="status" key={state.requestId}>
          <h2>{state.state === 'unknown' ? '结果无法确认' : state.state === 'unconfirmed' ? '结果待确认' : '正在取消'}</h2>
          <p>{state.state === 'unknown' ? 'Host 已退出，操作可能已完成。请核对输出文件或缓存状态。'
            : state.state === 'unconfirmed' ? 'Host 尚未返回最终结果，仍在等待确认。请勿重复执行该操作。'
            : '已发送取消请求，正在等待 Host 的最终结果。'}</p>
          {state.state === 'unknown' && <button className="button secondary" onClick={() =>
            void cacheManager.acknowledgeUncertain(state.requestId).catch(error => notify('error', describeError(error)))}>
            <Icon name="check" />已核对结果</button>}
        </section>)}

        <OperationProgress revision={progressRevision} />

        <section className="page-content">
          {(page === 'library' || page === 'settings') && scanReport && <section className="result-panel" aria-label="扫描结果">
            <div className="panel-heading"><h2>扫描结果</h2><span>损坏 {scanReport.invalidEntries ?? 0} · 无法访问 {scanReport.inaccessibleDirectories ?? 0} · 跳过未完成 {scanReport.skippedIncompleteEntries ?? 0}</span></div>
            {scanReport.issues.length > 0 && <details><summary>问题明细（{scanReport.issues.length}）</summary><ul className="result-list">
              {scanReport.issues.map((issue) => <li key={issue.id}><div><strong>{issue.kind}</strong><p>{issue.message}</p><code>{issue.path}</code></div>
                <button className="icon-button" title="打开所在目录" aria-label={`定位问题 ${issue.id + 1}`} disabled={uiBusy || indexBinding?.indexToken !== scanReport.indexToken}
                  onClick={() => void run('正在定位…', () => cacheManager.locateScanIssue(scanReport.indexToken, issue.id))}><Icon name="folder" /></button></li>)}
            </ul></details>}
            {scanReport.issuesTruncated && <p>仅展示前 100 条问题，汇总计数包含全部条目。</p>}
          </section>}
          {batchReport && batchReport.rootPath === activeRootPath && <section className="result-panel" aria-label="操作结果">
            <div className="panel-heading"><h2>{batchReport.kind === 'play' ? '播放' : '导出'}结果：{{ success: '全部成功', partial: '部分失败', failed: '失败', cancelled: '已取消', unknown: '结果无法确认' }[batchReport.status]}</h2>
              <button className="icon-button" title="关闭结果" aria-label="关闭操作结果" onClick={() => setBatchReport(null)}><Icon name="close" /></button></div>
            <p>{batchReport.kind === 'play' ? `已交给播放器 ${batchReport.succeeded} 项` : `已发布 ${batchReport.succeeded} 项`}，失败 {batchReport.failures.length} 项。</p>
            {batchReport.kind === 'export' && batchReport.status !== 'success' && batchReport.status !== 'unknown' && <p>本批导出未发布。重试将重新执行完整批次，并复用已生成的转码缓存。</p>}
            {batchReport.status === 'unknown' && <p>操作可能已经完成，请先核对输出文件或缓存状态。</p>}
            {batchReport.retryUnavailable && <p>{batchReport.retryUnavailable}</p>}
            {batchReport.failures.length > 0 && <FailureList failures={batchReport.failures} />}
            {batchReport.status !== 'success' && batchReport.targets.length > 0 && <button className="button secondary" disabled={uiBusy}
              onClick={() => void (batchReport.kind === 'play' ? play(batchReport.targets, batchReport) : exportMedia(batchReport.targets, batchReport))}><Icon name="refresh" />{batchReport.kind === 'play' ? '重试未成功项目' : '重试完整批次'}</button>}
          </section>}
          {page === 'library' && <LibraryPage
            settings={settings} updateSetting={updateSetting} browse={browse} search={search} searchInput={searchInput}
            items={items} selectedIds={selectedIds} setSelectedIds={setSelectedIds} focusedId={focusedId}
            focus={focusCache} focusedItem={focusedItem}
            focusedDetails={focusedDetails} detailsLoading={detailsLoading} detailsOffset={detailsOffset} setDetailsOffset={setDetailsOffset}
            selectedSegmentIds={selectedSegmentIds} setSelectedSegmentIds={setSelectedSegmentIds}
            cachePage={cachePage} pageTo={search} busy={uiBusy} play={play} exportMedia={exportMedia} moveToTrash={moveToTrash}
            clear={clearLibrary}
          />}
          {page === 'storage' && <StoragePage
            storage={storage}
            settings={settings}
            busy={uiBusy}
            inspectionDisabled={inspectionDisabled}
            refresh={refreshStorage}
            cleanup={cleanupTranscodeCache}
            clear={requestClearTranscodeCache}
            open={openTranscodeCache}
          />}
          {page === 'trash' && <TrashPage entries={trash} snapshot={trashSnapshot} pageTo={offset => void refreshTrash(false, offset, trashSnapshot?.snapshotToken)} selected={selectedTrashIds} setSelected={setSelectedTrashIds} busy={uiBusy || !trashSnapshot} inspectionDisabled={inspectionDisabled} canPurge={capabilities.trashPurge} refresh={refreshTrash} restore={() => {
            if (!selectedTrashIds.size) return notify('info', '请选择要恢复的条目。');
            const rootPath = trashState.rootPath;
            if (!rootPath || rootPath !== activeRootPath) return notify('error', '回收站内容与当前缓存目录不一致，请刷新后重试。');
            const entryIds = trash.filter((entry) => selectedTrashIds.has(entry.id)).map((entry) => entry.id);
            if (!entryIds.length) return notify('info', '请选择要恢复的条目。');
            void (async () => {
              const completed = await run('正在恢复缓存…', () => restoreAndRescan(rootPath, entryIds, settings.includeIncomplete));
              if (!completed) return;
              const restored = new Set(completed.result.restored);
              if (activeRootPathRef.current === rootPath) {
                setTrashState((current) => current.rootPath === rootPath
                  ? { rootPath, value: current.value.filter((entry) => !restored.has(entry.id)) }
                  : current);
              }
              invalidateStorage(); setSelectedTrashIds(new Set()); setUndoDeleteBatch(null);
              setTrashSnapshot(null);
              setTrashState({ rootPath: null, value: [] });
              if (completed.scanResult) {
                applyScanResult(rootPath, settings.includeIncomplete, completed.scanResult);
              }
              notify(diskOutcomeKind(completed.result), diskOutcomeMessage('恢复', completed.result.restored.length, completed.result));
            })();
          }} purge={(all) => setConfirm({
            title: (() => {
              const count = all ? (trashSnapshot?.totalItems ?? trash.length) : selectedTrashIds.size;
              return all ? `彻底清空回收站（${count} 项）` : `永久删除（${count} 项）`;
            })(),
            body: (() => {
              const chosen = all ? trash : trash.filter((entry) => selectedTrashIds.has(entry.id));
              return `缓存目录：${trashState.rootPath ?? '未加载'}；永久删除 ${all ? trashSnapshot?.totalItems ?? chosen.length : chosen.length} 项，共 ${formatBytes(all ? trashSnapshot?.totalSizeBytes ?? 0 : chosen.reduce((sum, entry) => sum + entry.sizeBytes, 0))}。此操作无法撤销。`;
            })(), destructive: true,
            action: () => { void (async () => {
              const rootPath = trashState.rootPath;
              const chosen = all ? trash : trash.filter((entry) => selectedTrashIds.has(entry.id));
              const ids = chosen.map((entry) => entry.id);
              if (!rootPath || rootPath !== activeRootPathRef.current) { notify('error', '缓存根目录已变化，已取消永久删除。'); return; }
              if (!ids.length) { notify('info', '没有可永久删除的条目。'); return; }
              const completed = await run('正在永久删除…', async () => {
                const result = all && trashSnapshot
                  ? await cacheManager.purgeTrashSnapshot(rootPath, trashSnapshot.snapshotToken)
                  : await cacheManager.purgeTrash(rootPath, ids);
                if (!result) return null;
                return { result };
              });
              if (!completed) return;
              const purged = new Set(completed.result.purged);
              if (activeRootPathRef.current === rootPath) {
                setTrashState((current) => current.rootPath === rootPath
                  ? { rootPath, value: current.value.filter((entry) => !purged.has(entry.id)) }
                  : current);
              }
              invalidateStorage(); setSelectedTrashIds(new Set());
              setTrashSnapshot(null);
              setTrashState({ rootPath: null, value: [] });
              notify(diskOutcomeKind(completed.result), diskOutcomeMessage('永久删除', completed.result.purged.length, completed.result));
            })(); },
          })} />}
          {page === 'settings' && <SettingsPage value={draftSettings} setValue={setDraftSettings} browse={async () => {
            const value = await run('正在选择缓存目录…', () => cacheManager.chooseRootDirectory(draftSettings.rootPath));
            if (value) setDraftSettings((current) => ({ ...current, rootPath: value }));
          }} save={async () => {
            const candidate = { ...draftSettings, rootPath: draftSettings.rootPath.trim() };
            const rootChanged = candidate.rootPath !== activeRootPath;
            const scanBehaviorChanged = candidate.includeIncomplete !== settings.includeIncomplete;
            const completed = await run(rootChanged ? '正在验证并切换缓存目录…' : '正在保存设置…', async () => {
              const scanResult = candidate.rootPath && (rootChanged || scanBehaviorChanged)
                ? await cacheManager.scan({
                  rootPath: candidate.rootPath,
                  includeIncomplete: candidate.includeIncomplete,
                  persistSettings: false,
                  offset: 0,
                  pageSize: DEFAULT_CACHE_PAGE_SIZE,
                })
                : null;
              if (scanResult) invalidateIndex();
              const saved = await cacheManager.updateSettings(candidate);
              if ((rootChanged || scanBehaviorChanged) && !scanResult) invalidateIndex();
              const sessionSettings = candidate.rootPath ? { ...saved, rootPath: candidate.rootPath } : saved;
              return { saved: sessionSettings, scanResult };
            });
            if (!completed) return;
            setSettings(completed.saved);
            setDraftSettings(completed.saved);
            setUndoDeleteBatch(null);
            if (rootChanged) invalidateRootViews();
            if (completed.scanResult) {
              applyScanResult(candidate.rootPath, candidate.includeIncomplete, completed.scanResult, '设置已保存。');
            }
            if (!completed.saved.rootPath.trim()) {
              setIndexBinding(null);
              replaceLibraryItems();
              notify('success', '设置已保存；缓存根目录为空，列表已清空。');
              return;
            }
            if (!completed.scanResult) {
              notify('success', '设置已保存。');
            }
          }} busy={uiBusy} />}
          {page === 'diagnostics' && <DiagnosticsPage health={health} desktop={desktop} activities={activities} refresh={async () => {
            const value = await run('正在检查运行环境…', () => cacheManager.health());
            if (value) { setHealth(value); notify(value.status === 'ok' ? 'success' : 'error', '运行环境检查完成。'); }
          }} exportReport={async () => {
            const value = await run('正在导出诊断报告…', () => cacheManager.exportDiagnostics(`BLCM-diagnostics-${dateStamp()}.zip`, activeRootPath || undefined));
            if (value) notify('success', `诊断报告已导出：${value.outputPath}`);
          }} busy={uiBusy} />}
        </section>

        <footer className="statusbar"><span>{inspectionBusy ?? busy ?? (detailsLoading ? '正在加载分段详情…' : items.length ? `当前显示 ${cachePage.offset + 1}–${cachePage.offset + items.length} / ${cachePage.totalItems} 条缓存，已选 ${selectedIds.size} 条 · ${formatBytes(selectedBytes(items, selectedIds))}` : '就绪')}</span><span>F5 扫描 · Ctrl+F 搜索 · Ctrl+Z 撤销 · Ctrl+E 导出 · Esc 取消</span></footer>
      </main>

      <div className="toast-stack" aria-live="polite">{notices.map((notice) => <div key={notice.id} className={`toast ${notice.kind}`}><Icon name={notice.kind === 'error' ? 'warning' : 'check'} /><span>{notice.message}</span></div>)}</div>
      {confirm && <Modal title={confirm.title} onClose={() => setConfirm(null)}><p>{confirm.body}</p><div className="modal-actions"><button className="button ghost" onClick={() => setConfirm(null)}>取消</button><button className={confirm.destructive ? 'button danger' : 'button primary'} onClick={() => { const action = confirm.action; setConfirm(null); action(); }}>确认</button></div></Modal>}
      {legacySettingsMigration && <Modal title="确认旧版缓存目录" onClose={() => undefined} closable={false}><p>旧版本记住了以下目录：</p><p className="migration-path">{legacySettingsMigration.rootPath}</p><p>请选择今后的启动行为。本次选择会保存，也可以稍后在“设置”中更改。</p><div className="modal-actions migration-actions"><button className="button ghost" disabled={Boolean(busy)} onClick={() => void resolveLegacySettingsMigration('forget')}>忘记目录</button><button className="button secondary" disabled={Boolean(busy)} onClick={() => void resolveLegacySettingsMigration('remember')}>仅记住，不扫描</button><button className="button primary" disabled={Boolean(busy)} onClick={() => void resolveLegacySettingsMigration('scan')}>启用并立即扫描</button></div></Modal>}
    </div>
  );
}


function isUnknownOutcome(error: unknown): boolean {
  return errorCode(error) === 'OUTCOME_UNKNOWN';
}

function isCancellationError(error: unknown): boolean {
  return ['cancelled', 'CANCELLED'].includes(errorCode(error) ?? '');
}
function isStaleIndexError(error: unknown): boolean {
  return errorCode(error) === 'stale_index';
}
function sameSearchRequest(left: SearchRequest, right: SearchRequest): boolean {
  return left.indexToken === right.indexToken &&
    left.pageSize === right.pageSize &&
    left.keyword === right.keyword &&
    left.matchMode === right.matchMode &&
    left.splitKeywords === right.splitKeywords &&
    left.anyKeywords === right.anyKeywords &&
    left.includePartName === right.includePartName &&
    left.includeOwnerName === right.includeOwnerName &&
    left.includeBvid === right.includeBvid &&
    left.includeAvid === right.includeAvid &&
    left.caseSensitive === right.caseSensitive;
}
function safeBootstrapError(error: string): string {
  return error
    .replace(/[A-Za-z]:\\[^\r\n]*/g, '[路径]')
    .replace(/\/(?:home|Users)\/[^\r\n]*/g, '[路径]')
    .replace(/[\r\n]+/g, ' ')
    .slice(0, 300);
}
function describeError(error: unknown): string { if (error instanceof Error) return error.message; if (typeof error === 'string') return error; return '操作失败，请导出诊断报告查看详情。'; }
function pageSubtitle(page: Page): string { return ({ library: '扫描、查找和管理本地 B 站缓存', storage: '了解原始缓存、转码产物与回收站占用', trash: '恢复误删条目或安全地永久清理', settings: '调整扫描、播放和缓存维护偏好', diagnostics: '检查桌面运行时、媒体工具链和最近操作' })[page]; }
function isEditable(target: EventTarget | null): boolean { return target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement || (target instanceof HTMLElement && target.isContentEditable); }
