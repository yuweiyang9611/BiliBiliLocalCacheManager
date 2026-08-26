export type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };

export type MatchMode = 'contains' | 'prefix' | 'exact';
export type PlayerPreference = 'system' | 'mpv' | 'vlc';

export interface AppSettings {
  rootPath: string;
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
}

export interface TrashEntry {
  id: string;
  avid: string;
  title: string;
  sizeBytes: number;
  deletedAt: string | null;
  originalPath?: string;
}

export interface HostHealth {
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
  trashPurge: boolean;
  nativeWayland: false;
}

export interface InitialState {
  settings: AppSettings;
  items: CacheEntry[];
  storage: StorageSnapshot;
  trash: TrashEntry[];
  capabilities: DesktopCapabilities;
}

export interface ScanResult {
  items: CacheEntry[];
  includedEntries?: number;
  skippedIncompleteEntries?: number;
  invalidEntries?: number;
  inaccessibleDirectories?: number;
}

export interface SearchRequest {
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

export interface SelectionTarget {
  avid: string;
  pageIndexes?: number[];
}

export interface HostProgress {
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
  health(): Promise<HostHealth>;
  getInitialState(): Promise<InitialState>;
  getSettings(): Promise<AppSettings>;
  updateSettings(patch: Partial<AppSettings>): Promise<AppSettings>;
  chooseRootDirectory(defaultPath?: string): Promise<string | null>;
  scan(options: { rootPath: string; includeIncomplete: boolean }): Promise<ScanResult>;
  cancel(): Promise<boolean>;
  search(request: SearchRequest): Promise<CacheEntry[]>;
  getStorage(): Promise<StorageSnapshot>;
  cleanupTranscodeCache(): Promise<ArtifactCleanupResult>;
  clearTranscodeCache(): Promise<ArtifactCleanupResult | null>;
  openTranscodeCache(): Promise<boolean>;
  moveToTrash(avids: string[]): Promise<{ moved: string[]; failed: string[] }>;
  listTrash(): Promise<TrashEntry[]>;
  restoreTrash(entryIds: string[]): Promise<{ restored: string[]; failed: string[] }>;
  purgeTrash(entryIds?: string[]): Promise<{ purged: string[]; failed: string[] }>;
  play(targets: SelectionTarget[], playerPreference: PlayerPreference): Promise<{ queued: number }>;
  exportMedia(targets: SelectionTarget[], suggestedName: string): Promise<{ outputPath: string } | null>;
  exportDiagnostics(suggestedName: string): Promise<{ outputPath: string } | null>;
  getDesktopInfo(): Promise<DesktopInfo>;
  onProgress(listener: (progress: HostProgress) => void): () => void;
  onHostUnavailable(listener: (message: string) => void): () => void;
}

export const defaultSettings: AppSettings = {
  rootPath: '',
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
