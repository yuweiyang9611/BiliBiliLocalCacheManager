import { describe, expect, it } from 'vitest';
import {
  defaultSettings,
  DESKTOP_HOST_PROTOCOL_VERSION,
  emptyStorage,
} from '../shared/contracts';
import { validateCacheDetails, validateCachePage, validateInitialState } from './host-contract-validation';

describe('validateInitialState', () => {
  it('accepts and maps a complete protocol v2 initial state', () => {
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
      expected: /initialState\.protocolVersion 必须为 2/,
    },
  ])('rejects $label', ({ value, expected }) => {
    expect(() => validateInitialState(value())).toThrow(expected);
  });
});

describe('paged Host responses', () => {
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
