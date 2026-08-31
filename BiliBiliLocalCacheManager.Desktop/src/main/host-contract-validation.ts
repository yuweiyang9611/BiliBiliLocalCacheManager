import type {
  AppSettings,
  CacheDetails,
  CacheEntry,
  CachePage,
  CacheSegment,
  DesktopCapabilities,
  HostHealth,
  InitialState,
  ScanResult,
  SettingsStateInfo,
  StorageArea,
  StorageSnapshot,
  TrashEntry,
} from '../shared/contracts';
import {
  DESKTOP_HOST_PROTOCOL_VERSION,
  MAXIMUM_CACHE_PAGE_SIZE,
} from '../shared/contracts';
import { isRecord } from './protocol';

const maximumWireTextLength = 4096;

export function validateHostHealth(value: unknown): HostHealth {
  const source = record(value, 'health');
  assertProtocolVersion(source, 'health');
  const status = string(source.status, 'health.status');
  if (status !== 'ok' && status !== 'degraded') {
    invalid('health.status 必须是 ok 或 degraded');
  }
  if (source.warnings !== undefined) {
    stringArray(source.warnings, 'health.warnings', 1_000);
  }
  return source as unknown as HostHealth;
}

export function validateInitialState(value: unknown): InitialState {
  const source = record(value, 'initialState');
  assertProtocolVersion(source, 'initialState');
  return {
    protocolVersion: DESKTOP_HOST_PROTOCOL_VERSION,
    settings: appSettings(source.settings),
    settingsState: settingsState(source.settingsState),
    items: array(source.items, 'initialState.items', MAXIMUM_CACHE_PAGE_SIZE).map((item, index) =>
      cacheEntry(item, `initialState.items[${index}]`)),
    storage: storageSnapshot(source.storage),
    trash: array(source.trash, 'initialState.trash', 10_000).map((item, index) =>
      trashEntry(item, `initialState.trash[${index}]`)),
    capabilities: capabilities(source.capabilities),
  };
}

export function validateScanResult(value: unknown): ScanResult {
  const source = record(value, 'scan');
  const page = cachePage(source, 'scan');
  return {
    ...page,
    ...(source.rootPath === undefined ? {} : { rootPath: string(source.rootPath, 'scan.rootPath', 32_768) }),
    ...(source.includeIncomplete === undefined ? {} : { includeIncomplete: boolean(source.includeIncomplete, 'scan.includeIncomplete') }),
    ...(source.scannedAvidDirectories === undefined ? {} : { scannedAvidDirectories: integer(source.scannedAvidDirectories, 'scan.scannedAvidDirectories') }),
    ...(source.scannedSegmentDirectories === undefined ? {} : { scannedSegmentDirectories: integer(source.scannedSegmentDirectories, 'scan.scannedSegmentDirectories') }),
    ...(source.includedEntries === undefined ? {} : { includedEntries: integer(source.includedEntries, 'scan.includedEntries') }),
    ...(source.skippedIncompleteEntries === undefined ? {} : { skippedIncompleteEntries: integer(source.skippedIncompleteEntries, 'scan.skippedIncompleteEntries') }),
    ...(source.invalidEntries === undefined ? {} : { invalidEntries: integer(source.invalidEntries, 'scan.invalidEntries') }),
    ...(source.inaccessibleDirectories === undefined ? {} : { inaccessibleDirectories: integer(source.inaccessibleDirectories, 'scan.inaccessibleDirectories') }),
    ...(source.hasWarnings === undefined ? {} : { hasWarnings: boolean(source.hasWarnings, 'scan.hasWarnings') }),
    ...(source.completedAtUtc === undefined ? {} : { completedAtUtc: string(source.completedAtUtc, 'scan.completedAtUtc') }),
  };
}

export function validateCachePage(value: unknown): CachePage {
  return cachePage(record(value, 'search'), 'search');
}

export function validateCacheDetails(value: unknown): CacheDetails {
  const source = record(value, 'cache.details');
  const indexToken = token(source.indexToken, 'cache.details.indexToken');
  const avid = string(source.avid, 'cache.details.avid', 64);
  const item = cacheEntry(source.item, 'cache.details.item');
  const pagination = paginationFields(source, 'cache.details');
  const segments = array(source.segments, 'cache.details.segments', MAXIMUM_CACHE_PAGE_SIZE)
    .map((segment, index) => cacheSegment(segment, `cache.details.segments[${index}]`));
  if (segments.length > pagination.pageSize || item.avid !== avid) {
    invalid('cache.details 的分段页或 avid 与摘要不一致');
  }
  assertPaginationInvariants(pagination, segments.length, 'cache.details');
  return { indexToken, avid, item, ...pagination, segments };
}

function cachePage(source: Record<string, unknown>, label: string): CachePage {
  const indexToken = token(source.indexToken, `${label}.indexToken`);
  const pagination = paginationFields(source, label);
  const items = array(source.items, `${label}.items`, MAXIMUM_CACHE_PAGE_SIZE)
    .map((item, index) => cacheEntry(item, `${label}.items[${index}]`));
  assertPaginationInvariants(pagination, items.length, label);
  return { indexToken, ...pagination, items };
}

function paginationFields(source: Record<string, unknown>, label: string) {
  const offset = integer(source.offset, `${label}.offset`);
  const pageSize = integer(source.pageSize, `${label}.pageSize`, 1, MAXIMUM_CACHE_PAGE_SIZE);
  const totalItems = integer(source.totalItems, `${label}.totalItems`);
  const hasMore = boolean(source.hasMore, `${label}.hasMore`);
  return { offset, pageSize, totalItems, hasMore };
}

function cacheEntry(value: unknown, label: string): CacheEntry {
  const source = record(value, label);
  if ('segments' in source) invalid(`${label} 不得携带 segments；请使用 cache.details`);
  return {
    id: string(source.id, `${label}.id`, 64),
    avid: string(source.avid, `${label}.avid`, 64),
    bvid: string(source.bvid, `${label}.bvid`),
    title: string(source.title, `${label}.title`),
    ownerName: string(source.ownerName, `${label}.ownerName`),
    durationSeconds: number(source.durationSeconds, `${label}.durationSeconds`),
    segmentCount: integer(source.segmentCount, `${label}.segmentCount`),
    sizeBytes: integer(source.sizeBytes, `${label}.sizeBytes`, 0, Number.MAX_SAFE_INTEGER),
    isAllCompleted: boolean(source.isAllCompleted, `${label}.isAllCompleted`),
    lastUpdated: nullableString(source.lastUpdated, `${label}.lastUpdated`),
  };
}

function cacheSegment(value: unknown, label: string): CacheSegment {
  const source = record(value, label);
  return {
    id: string(source.id, `${label}.id`),
    segmentKey: string(source.segmentKey, `${label}.segmentKey`),
    pageIndex: integer(source.pageIndex, `${label}.pageIndex`),
    partName: string(source.partName, `${label}.partName`),
    structureKind: string(source.structureKind, `${label}.structureKind`),
    materialKind: string(source.materialKind, `${label}.materialKind`),
    sizeBytes: integer(source.sizeBytes, `${label}.sizeBytes`, 0, Number.MAX_SAFE_INTEGER),
    durationSeconds: number(source.durationSeconds, `${label}.durationSeconds`),
    isPlayable: boolean(source.isPlayable, `${label}.isPlayable`),
    ...(source.directoryPath === undefined || source.directoryPath === null
      ? {}
      : { directoryPath: string(source.directoryPath, `${label}.directoryPath`, 32_768) }),
  };
}

function appSettings(value: unknown): AppSettings {
  const source = record(value, 'initialState.settings');
  const matchMode = string(source.matchMode, 'initialState.settings.matchMode');
  if (matchMode !== 'contains' && matchMode !== 'prefix' && matchMode !== 'exact') invalid('settings.matchMode 无效');
  const playerPreference = string(source.playerPreference, 'initialState.settings.playerPreference');
  if (playerPreference !== 'system' && playerPreference !== 'mpv' && playerPreference !== 'vlc') invalid('settings.playerPreference 无效');
  return {
    rootPath: string(source.rootPath, 'initialState.settings.rootPath', 32_768),
    rememberRootPath: boolean(source.rememberRootPath, 'initialState.settings.rememberRootPath'),
    scanOnStartup: boolean(source.scanOnStartup, 'initialState.settings.scanOnStartup'),
    includeIncomplete: boolean(source.includeIncomplete, 'initialState.settings.includeIncomplete'),
    keyword: string(source.keyword, 'initialState.settings.keyword', 500),
    splitKeywords: boolean(source.splitKeywords, 'initialState.settings.splitKeywords'),
    anyKeywords: boolean(source.anyKeywords, 'initialState.settings.anyKeywords'),
    includePartName: boolean(source.includePartName, 'initialState.settings.includePartName'),
    includeOwnerName: boolean(source.includeOwnerName, 'initialState.settings.includeOwnerName'),
    includeBvid: boolean(source.includeBvid, 'initialState.settings.includeBvid'),
    includeAvid: boolean(source.includeAvid, 'initialState.settings.includeAvid'),
    caseSensitive: boolean(source.caseSensitive, 'initialState.settings.caseSensitive'),
    matchMode,
    playerPreference,
    transcodeCacheRetentionDays: integer(source.transcodeCacheRetentionDays, 'initialState.settings.transcodeCacheRetentionDays', 1, 1_825),
    transcodeCacheMaxSizeGigabytes: integer(source.transcodeCacheMaxSizeGigabytes, 'initialState.settings.transcodeCacheMaxSizeGigabytes', 1, 128),
  };
}

function settingsState(value: unknown): SettingsStateInfo {
  const source = record(value, 'initialState.settingsState');
  const schema = source.sourceSchemaVersion;
  if (schema !== null && schema !== undefined) integer(schema, 'initialState.settingsState.sourceSchemaVersion');
  return {
    canSave: boolean(source.canSave, 'initialState.settingsState.canSave'),
    sourceSchemaVersion: schema === undefined ? null : schema as number | null,
    ...(source.message === undefined || source.message === null
      ? {}
      : { message: string(source.message, 'initialState.settingsState.message') }),
  };
}

function capabilities(value: unknown): DesktopCapabilities {
  const source = record(value, 'initialState.capabilities');
  const nativeWayland = boolean(source.nativeWayland, 'initialState.capabilities.nativeWayland');
  if (nativeWayland !== false) invalid('initialState.capabilities.nativeWayland 必须为 false');
  return {
    playback: boolean(source.playback, 'initialState.capabilities.playback'),
    exportMedia: boolean(source.exportMedia, 'initialState.capabilities.exportMedia'),
    cacheDetails: boolean(source.cacheDetails, 'initialState.capabilities.cacheDetails'),
    trashPurge: boolean(source.trashPurge, 'initialState.capabilities.trashPurge'),
    nativeWayland,
  };
}

function assertPaginationInvariants(
  page: { offset: number; pageSize: number; totalItems: number; hasMore: boolean },
  itemCount: number,
  label: string,
): void {
  const remaining = Math.max(page.totalItems - page.offset, 0);
  const expectedItemCount = Math.min(page.pageSize, remaining);
  const expectedHasMore = page.offset < page.totalItems && page.pageSize < remaining;
  if (itemCount !== expectedItemCount) {
    invalid(`${label} 项数 ${itemCount} 与 offset/pageSize/totalItems 不一致`);
  }
  if (page.hasMore !== expectedHasMore) {
    invalid(`${label}.hasMore 与 offset/pageSize/totalItems 不一致`);
  }
}

function storageSnapshot(value: unknown): StorageSnapshot {
  const source = record(value, 'initialState.storage');
  return {
    originalCache: storageArea(source.originalCache, 'initialState.storage.originalCache'),
    transcodeCache: storageArea(source.transcodeCache, 'initialState.storage.transcodeCache'),
    trash: storageArea(source.trash, 'initialState.storage.trash'),
    totalBytes: integer(source.totalBytes, 'initialState.storage.totalBytes', 0, Number.MAX_SAFE_INTEGER),
    ...(source.lastMaintenanceSummary === undefined || source.lastMaintenanceSummary === null
      ? {}
      : { lastMaintenanceSummary: string(source.lastMaintenanceSummary, 'initialState.storage.lastMaintenanceSummary') }),
  };
}

function storageArea(value: unknown, label: string): StorageArea {
  const source = record(value, label);
  return {
    bytes: integer(source.bytes, `${label}.bytes`, 0, Number.MAX_SAFE_INTEGER),
    itemCount: integer(source.itemCount, `${label}.itemCount`),
    ...(source.path === undefined || source.path === null ? {} : { path: string(source.path, `${label}.path`, 32_768) }),
  };
}

function trashEntry(value: unknown, label: string): TrashEntry {
  const source = record(value, label);
  return {
    id: string(source.id, `${label}.id`),
    avid: string(source.avid, `${label}.avid`, 64),
    title: string(source.title, `${label}.title`),
    sizeBytes: integer(source.sizeBytes, `${label}.sizeBytes`, 0, Number.MAX_SAFE_INTEGER),
    deletedAt: nullableString(source.deletedAt, `${label}.deletedAt`),
    ...(source.originalPath === undefined || source.originalPath === null
      ? {}
      : { originalPath: string(source.originalPath, `${label}.originalPath`, 32_768) }),
  };
}

function assertProtocolVersion(source: Record<string, unknown>, label: string): void {
  if (source.protocolVersion !== DESKTOP_HOST_PROTOCOL_VERSION) {
    invalid(`${label}.protocolVersion 必须为 ${DESKTOP_HOST_PROTOCOL_VERSION}`);
  }
}

function record(value: unknown, label: string): Record<string, unknown> {
  if (!isRecord(value)) invalid(`${label} 必须是对象`);
  return value;
}

function array(value: unknown, label: string, maximum: number): unknown[] {
  if (!Array.isArray(value) || value.length > maximum) invalid(`${label} 必须是最多 ${maximum} 项的数组`);
  return value;
}

function stringArray(value: unknown, label: string, maximum: number): string[] {
  return array(value, label, maximum).map((item, index) => string(item, `${label}[${index}]`));
}

function string(value: unknown, label: string, maximum = maximumWireTextLength): string {
  if (typeof value !== 'string' || value.length > maximum) invalid(`${label} 必须是长度不超过 ${maximum} 的字符串`);
  return value;
}

function token(value: unknown, label: string): string {
  const result = string(value, label, 128);
  if (!result) invalid(`${label} 不能为空`);
  return result;
}

function nullableString(value: unknown, label: string): string | null {
  return value === null ? null : string(value, label);
}

function boolean(value: unknown, label: string): boolean {
  if (typeof value !== 'boolean') invalid(`${label} 必须是布尔值`);
  return value;
}

function number(value: unknown, label: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) invalid(`${label} 必须是非负有限数字`);
  return value;
}

function integer(value: unknown, label: string, minimum = 0, maximum = 2_147_483_647): number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum || (value as number) > maximum) {
    invalid(`${label} 必须是 ${minimum}–${maximum} 之间的整数`);
  }
  return value as number;
}

function invalid(message: string): never {
  throw new TypeError(`Desktop Host 返回的数据无效：${message}。`);
}
