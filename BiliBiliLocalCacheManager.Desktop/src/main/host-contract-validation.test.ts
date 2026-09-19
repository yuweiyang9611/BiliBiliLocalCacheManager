import { describe, expect, it } from 'vitest';
import {
  defaultSettings,
  DESKTOP_HOST_PROTOCOL_VERSION,
  emptyStorage,
} from '../shared/contracts';
import {
  validateCacheDetails, validateCachePage, validateInitialState, validatePlaybackBatchResult,
  validateExportBatchResult, validateScanResult, validateArtifactCleanupResult,
  validateTrashMoveResult, validateTrashRestoreResult, validateTrashPurgeResult,
} from './host-contract-validation';

describe('validateInitialState', () => {
  it('accepts and maps a complete protocol v3 initial state', () => {
    const value = validInitialState();

    expect(validateInitialState(value)).toEqual(value);
  });

  it.each<{
    label: string;
    value(): unknown;
    expected: RegExp;
  }>([
    {
      label: 'null',
      value: () => null,
      expected: /initialState 必须是对象/,
    },
    {
      label: 'missing settings',
      value: () => without(validInitialState(), 'settings'),
      expected: /initialState\.settings 必须是对象/,
    },
    {
      label: 'string scanOnStartup',
      value: () => withNested(validInitialState(), 'settings', 'scanOnStartup', 'true'),
      expected: /initialState\.settings\.scanOnStartup 必须是布尔值/,
    },
    {
      label: 'non-array items',
      value: () => ({ ...validInitialState(), items: {} }),
      expected: /initialState\.items 必须是最多 200 项的数组/,
    },
    {
      label: 'capabilities missing exportMedia',
      value: () => withoutNested(validInitialState(), 'capabilities', 'exportMedia'),
      expected: /initialState\.capabilities\.exportMedia 必须是布尔值/,
    },
    {
      label: 'wrong protocol version',
      value: () => ({ ...validInitialState(), protocolVersion: DESKTOP_HOST_PROTOCOL_VERSION + 1 }),
      expected: /initialState\.protocolVersion 必须为 3/,
    },
  ])('rejects $label', ({ value, expected }) => {
    expect(() => validateInitialState(value())).toThrow(expected);
  });
});

describe('paged Host responses', () => {
  it('requires bounded scan issues and structured media outcomes', () => {
    expect(() => validateScanResult(validCachePage())).toThrow(/issues/);
    expect(() => validateScanResult({ ...validCachePage(), issues: Array(101).fill({}), issuesTruncated: true })).toThrow(/100/);
    expect(validatePlaybackBatchResult({ queued: 0, failures: [{ avid: '100', pageIndex: 1, title: '', message: 'Unavailable' }] }).failures).toHaveLength(1);
    expect(() => validateExportBatchResult({ published: false, outputPath: '/partial.mp4', exportedCount: 0, failures: [] })).toThrow(/inconsistent/);
  });
  it('rejects inconsistent item counts and hasMore flags', () => {
    const page = validCachePage();
    expect(() => validateCachePage({ ...page, totalItems: 3, hasMore: false })).toThrow(/hasMore/);
    expect(() => validateCachePage({ ...page, items: [page.items[0]] })).toThrow(/项数/);
  });

  it('rejects eager segments on cache summaries', () => {
    const page = validCachePage();
    const item = page.items[0] as Record<string, unknown>;
    expect(() => validateCachePage({
      ...page,
      items: [{ ...item, segments: [] }, page.items[1]],
    })).toThrow(/不得携带 segments/);
  });

  it('validates segment-page cross invariants', () => {
    const details = {
      indexToken: 'index-token',
      avid: '100',
      item: validCacheEntry('100'),
      offset: 0,
      pageSize: 2,
      totalItems: 1,
      hasMore: false,
      segments: [],
    };
    expect(() => validateCacheDetails(details)).toThrow(/项数/);
  });
});

describe('disk mutation outcomes', () => {
  const trashValidators = [
    { operation: 'move', key: 'moved', validate: validateTrashMoveResult, limit: 1_000 },
    { operation: 'restore', key: 'restored', validate: validateTrashRestoreResult, limit: 1_000 },
    { operation: 'purge', key: 'purged', validate: validateTrashPurgeResult, limit: 10_000 },
  ];

  it.each(trashValidators)('preserves completed and unprocessed $operation items on cancellation', ({ key, validate }) => {
    const value = { [key]: ['100'], failed: ['200'], cancelled: true, unprocessed: ['300'] };
    expect(validate(value)).toEqual(value);
  });

  it.each(trashValidators)('accepts older v3 $operation results without cancellation fields', ({ key, validate }) => {
    const value = { [key]: ['100'], failed: [] };
    expect(validate(value)).toEqual(value);
  });

  it.each(trashValidators)('rejects malformed $operation cancellation flags and identifiers', ({ key, validate, limit }) => {
    const value = { [key]: [], failed: [] };
    expect(() => validate({ ...value, cancelled: 'true' })).toThrow(/cancelled/);
    expect(() => validate({ ...value, unprocessed: '100' })).toThrow(/unprocessed/);
    expect(() => validate({ ...value, unprocessed: [100] })).toThrow(/unprocessed/);
    expect(() => validate({ ...value, unprocessed: [''] })).toThrow(/unprocessed/);
    expect(() => validate({ ...value, unprocessed: ['bad\0id'] })).toThrow(/unprocessed/);
    expect(() => validate({ ...value, unprocessed: Array(limit + 1).fill('100') })).toThrow(/unprocessed/);
    expect(() => validate({ ...value, [key]: [false] })).toThrow();
    expect(() => validate({ ...value, failed: [null] })).toThrow(/failed/);
  });

  it('preserves partial artifact cleanup results and accepts older v3 results', () => {
    const legacy = { deletedFileCount: 2, freedBytes: 123, failedFileCount: 1, remainingBytes: 456 };
    expect(validateArtifactCleanupResult(legacy)).toEqual(legacy);
    const partial = { ...legacy, cancelled: true, unprocessedFileCount: 3, remainingBytesEstimated: true };
    expect(validateArtifactCleanupResult(partial)).toEqual(partial);
  });

  it.each([
    ['cancelled', 'true'],
    ['remainingBytesEstimated', 1],
    ['unprocessedFileCount', -1],
    ['unprocessedFileCount', 1.5],
    ['deletedFileCount', -1],
    ['failedFileCount', NaN],
    ['freedBytes', Number.MAX_SAFE_INTEGER + 1],
    ['remainingBytes', Infinity],
  ])('rejects an invalid artifact %s field: %s', (key, value) => {
    const response = { deletedFileCount: 2, freedBytes: 123, failedFileCount: 1, remainingBytes: 456, [key as string]: value };
    expect(() => validateArtifactCleanupResult(response)).toThrow();
  });
});

function validInitialState(): Record<string, unknown> {
  return {
    protocolVersion: DESKTOP_HOST_PROTOCOL_VERSION,
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
    }],
    storage: {
      ...emptyStorage,
      originalCache: { ...emptyStorage.originalCache },
      transcodeCache: { ...emptyStorage.transcodeCache },
      trash: { ...emptyStorage.trash },
    },
    trash: [{
      id: 'trash-100',
      avid: '100',
      title: '旧缓存',
      sizeBytes: 1024,
      deletedAt: null,
    }],
    capabilities: {
      playback: true,
      exportMedia: true,
      cacheDetails: true,
      trashPurge: false,
      nativeWayland: false,
    },
  };
}

function validCachePage() {
  return {
    indexToken: 'index-token',
    offset: 0,
    pageSize: 2,
    totalItems: 2,
    hasMore: false,
    items: [validCacheEntry('100'), validCacheEntry('200')],
  };
}

function validCacheEntry(avid: string) {
  return {
    id: avid,
    avid,
    bvid: `BV${avid}`,
    title: `缓存 ${avid}`,
    ownerName: '测试 UP',
    durationSeconds: 125,
    segmentCount: 1,
    sizeBytes: 1024,
    isAllCompleted: true,
    lastUpdated: null,
  };
}

function without(source: Record<string, unknown>, key: string): Record<string, unknown> {
  const result = { ...source };
  delete result[key];
  return result;
}

function withNested(
  source: Record<string, unknown>,
  parent: string,
  key: string,
  value: unknown,
): Record<string, unknown> {
  return {
    ...source,
    [parent]: {
      ...(source[parent] as Record<string, unknown>),
      [key]: value,
    },
  };
}

function withoutNested(
  source: Record<string, unknown>,
  parent: string,
  key: string,
): Record<string, unknown> {
  const nested = { ...(source[parent] as Record<string, unknown>) };
  delete nested[key];
  return { ...source, [parent]: nested };
}
