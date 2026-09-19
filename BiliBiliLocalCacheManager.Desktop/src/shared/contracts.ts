export type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };

export type MatchMode = 'contains' | 'prefix' | 'exact';
export type PlayerPreference = 'system' | 'mpv' | 'vlc';

export const DESKTOP_HOST_PROTOCOL_VERSION = 3 as const;
export const DEFAULT_CACHE_PAGE_SIZE = 100;
export const MAXIMUM_CACHE_PAGE_SIZE = 200;

export interface AppSettings {
  rootPath: string;
  rememberRootPath: boolean;
  scanOnStartup: boolean;
  includeIncomplete: boolean;
  keyword: string;
  splitKeywords: boolean;
  anyKeywords: boolean;
  includePartName: boolean;
  includeOwnerName: boolean;
  includeBvid: boolean;
  includeAvid: boolean;
  caseSensitive: boolean;
  matchMode: MatchMode;
  playerPreference: PlayerPreference;
  transcodeCacheRetentionDays: number;
  transcodeCacheMaxSizeGigabytes: number;
}

export interface SettingsStateInfo {
  canSave: boolean;
  sourceSchemaVersion: number | null;
  message?: string;
}

export interface CacheSegment {
  id: string;
  segmentKey: string;
  pageIndex: number;
  partName: string;
  structureKind: string;
  materialKind: string;
  sizeBytes: number;
  durationSeconds: number;
  isPlayable: boolean;
  directoryPath?: string;
}

export interface CacheEntry {
  id: string;
  avid: string;
  bvid: string;
  title: string;
  ownerName: string;
  durationSeconds: number;
  segmentCount: number;
  sizeBytes: number;
  isAllCompleted: boolean;
  lastUpdated: string | null;
}

export interface CachePage {
  indexToken: string;
  offset: number;
  pageSize: number;
  totalItems: number;
  hasMore: boolean;
  items: CacheEntry[];
}

export interface CacheDetails {
  indexToken: string;
  avid: string;
  item: CacheEntry;
  offset: number;
  pageSize: number;
  totalItems: number;
  hasMore: boolean;
  segments: CacheSegment[];
}

export interface StorageArea {
  bytes: number;
  itemCount: number;
  path?: string;
}

export interface StorageSnapshot {
  originalCache: StorageArea;
  transcodeCache: StorageArea;
  trash: StorageArea;
  totalBytes: number;
  lastMaintenanceSummary?: string;
}

export interface ArtifactCleanupResult {
  deletedFileCount: number;
  freedBytes: number;
  failedFileCount: number;
  remainingBytes: number;
  cancelled?: boolean;
  unprocessedFileCount?: number;
  remainingBytesEstimated?: boolean;
}

export interface TrashMoveResult { moved: string[]; failed: string[]; cancelled?: boolean; unprocessed?: string[] }
export interface TrashRestoreResult { restored: string[]; failed: string[]; cancelled?: boolean; unprocessed?: string[] }
export interface TrashPurgeResult { purged: string[]; failed: string[]; cancelled?: boolean; unprocessed?: string[] }

export interface TrashEntry {
  id: string;
  avid: string;
  title: string;
  sizeBytes: number;
  deletedAt: string | null;
  originalPath?: string;
}

export interface HostHealth {
  protocolVersion: typeof DESKTOP_HOST_PROTOCOL_VERSION;
  status: 'ok' | 'degraded';
  version?: string;
  runtime?: string;
  platform?: string;
  ffmpeg?: string;
  warnings?: string[];
}

export interface DesktopCapabilities {
  playback: boolean;
  exportMedia: boolean;
  cacheDetails: boolean;
  trashPurge: boolean;
  nativeWayland: false;
}

export interface InitialState {
  protocolVersion: typeof DESKTOP_HOST_PROTOCOL_VERSION;
  settings: AppSettings;
  settingsState: SettingsStateInfo;
  items: CacheEntry[];
  storage: StorageSnapshot;
  trash: TrashEntry[];
  capabilities: DesktopCapabilities;
}

export interface ScanResult extends CachePage {
  issues: ScanIssue[];
  issuesTruncated: boolean;
  rootPath?: string;
  includeIncomplete?: boolean;
  scannedAvidDirectories?: number;
  scannedSegmentDirectories?: number;
  includedEntries?: number;
  skippedIncompleteEntries?: number;
  invalidEntries?: number;
  inaccessibleDirectories?: number;
  hasWarnings?: boolean;
  completedAtUtc?: string;
}

export interface ScanIssue { id: number; kind: string; path: string; message: string }
export interface MediaFailure { avid: string; pageIndex: number | null; title: string; message: string }
export interface PlaybackBatchResult { queued: number; failures: MediaFailure[] }
export interface ExportBatchResult { outputPath: string | null; exportedCount: number; failures: MediaFailure[]; published: boolean }

export interface SearchRequest {
  indexToken: string;
  offset: number;
  pageSize: number;
  keyword: string;
  matchMode: MatchMode;
  splitKeywords: boolean;
  anyKeywords: boolean;
  includePartName: boolean;
  includeOwnerName: boolean;
  includeBvid: boolean;
  includeAvid: boolean;
  caseSensitive: boolean;
}

export interface CacheDetailsRequest {
  indexToken: string;
  avid: string;
  offset: number;
  pageSize: number;
}

export interface SelectionTarget {
  avid: string;
  pageIndexes?: number[];
}

export interface HostProgress {
  phase?: 'scan' | 'copy' | 'measure' | 'prepare' | 'probe' | 'concat' | 'mux' | 'fallback';
  requestId: string;
  operation: string;
  stage: string;
  percentage?: number;
  current?: number;
  total?: number;
  message?: string;
}

export interface DesktopInfo {
  appVersion: string;
  electronVersion: string;
  chromiumVersion: string;
  nodeVersion: string;
  platform: 'win32' | 'linux';
  arch: 'x64';
  displayBackend: 'win32' | 'x11';
}

export interface CacheManagerApi {
  cancelSearch(): Promise<boolean>;
  acknowledgeUncertain(requestId: string): Promise<boolean>;
  onOperationState(listener: (state: OperationState) => void): () => void;
  getOperationStates(): Promise<OperationState[]>;
  health(): Promise<HostHealth>;
  getInitialState(): Promise<InitialState>;
  getSettings(): Promise<AppSettings>;
  updateSettings(patch: Partial<AppSettings>): Promise<AppSettings>;
  chooseRootDirectory(defaultPath?: string): Promise<string | null>;
  scan(options: { rootPath: string; includeIncomplete: boolean; persistSettings?: boolean; offset?: number; pageSize?: number }): Promise<ScanResult>;
  cancel(): Promise<boolean>;
  locateScanIssue(indexToken: string, issueId: number): Promise<boolean>;
  search(request: SearchRequest): Promise<CachePage>;
  getCacheDetails(request: CacheDetailsRequest): Promise<CacheDetails>;
  cancelCacheDetails(): Promise<boolean>;
  getStorage(rootPath?: string): Promise<StorageSnapshot>;
  cleanupTranscodeCache(): Promise<ArtifactCleanupResult>;
  clearTranscodeCache(): Promise<ArtifactCleanupResult | null>;
  openTranscodeCache(): Promise<boolean>;
  moveToTrash(rootPath: string, avids: string[]): Promise<TrashMoveResult>;
  listTrash(rootPath: string): Promise<TrashEntry[]>;
  restoreTrash(rootPath: string, entryIds: string[]): Promise<TrashRestoreResult>;
  purgeTrash(rootPath: string, entryIds: string[]): Promise<TrashPurgeResult>;
  play(rootPath: string, targets: SelectionTarget[], playerPreference: PlayerPreference, includeIncomplete: boolean): Promise<PlaybackBatchResult>;
  exportMedia(rootPath: string, targets: SelectionTarget[], suggestedName: string, includeIncomplete: boolean): Promise<ExportBatchResult | null>;
  exportDiagnostics(suggestedName: string, rootPath?: string): Promise<{ outputPath: string } | null>;
  getDesktopInfo(): Promise<DesktopInfo>;
  onProgress(listener: (progress: HostProgress) => void): () => void;
  onHostUnavailable(listener: (message: string) => void): () => void;
}

export interface OperationState {
  requestId: string;
  operation: string;
  state: 'cancelling' | 'unconfirmed' | 'unknown' | 'settled';
  sideEffects: boolean;
}

export const defaultSettings: AppSettings = {
  rootPath: '',
  rememberRootPath: true,
  scanOnStartup: false,
  includeIncomplete: false,
  keyword: '',
  splitKeywords: true,
  anyKeywords: false,
  includePartName: true,
  includeOwnerName: true,
  includeBvid: true,
  includeAvid: true,
  caseSensitive: false,
  matchMode: 'contains',
  playerPreference: 'system',
  transcodeCacheRetentionDays: 30,
  transcodeCacheMaxSizeGigabytes: 10,
};

export const emptyStorage: StorageSnapshot = {
  originalCache: { bytes: 0, itemCount: 0 },
  transcodeCache: { bytes: 0, itemCount: 0 },
  trash: { bytes: 0, itemCount: 0 },
  totalBytes: 0,
};
