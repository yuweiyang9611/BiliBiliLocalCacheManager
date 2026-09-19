import { describe, expect, it } from 'vitest';
import type { MediaFailure } from '../shared/contracts';
import { buildPlaybackRetryTargets } from './playback-retry-targets';

function failure(avid: string, pageIndex: number | null): MediaFailure {
  return { avid, pageIndex, title: `Video ${avid}`, message: 'Preparation failed.' };
}

function pages(avid: string, count: number): MediaFailure[] {
  return Array.from({ length: count }, (_, index) => failure(avid, index));
}

describe('buildPlaybackRetryTargets', () => {
  it('groups more than 1000 failed pages into one target without mutating the failures', () => {
    const failures = pages('123', 1001);
    const original = structuredClone(failures);

    expect(buildPlaybackRetryTargets(failures)).toEqual([
      { avid: '123', pageIndexes: Array.from({ length: 1001 }, (_, index) => index) },
    ]);
    expect(failures).toEqual(original);
  });

  it('deduplicates pages while preserving the first avid and page appearance order', () => {
    expect(buildPlaybackRetryTargets([
      failure('222', 5), failure('111', 2), failure('222', 1),
      failure('111', 2), failure('222', 5), failure('111', 3),
    ])).toEqual([
      { avid: '222', pageIndexes: [5, 1] },
      { avid: '111', pageIndexes: [2, 3] },
    ]);
  });

  it('keeps a whole-video failure authoritative before or after specific page failures', () => {
    expect(buildPlaybackRetryTargets([
      failure('222', 5), failure('111', null), failure('222', null),
      failure('111', 2), failure('222', 1), failure('111', null),
    ])).toEqual([{ avid: '222' }, { avid: '111' }]);
  });

  it('splits more than 10000 pages for one avid without broadening the failed selection', () => {
    const targets = buildPlaybackRetryTargets(pages('123', 10001));

    expect(targets).toHaveLength(2);
    expect(targets[0]).toEqual({
      avid: '123', pageIndexes: Array.from({ length: 10000 }, (_, index) => index),
    });
    expect(targets[1]).toEqual({ avid: '123', pageIndexes: [10000] });
  });

  it('accepts 20000 unique pages and rejects an aggregate exceeding that limit', () => {
    const failures = [...pages('111', 10000), ...pages('222', 10000)];

    expect(buildPlaybackRetryTargets([...failures, failure('222', 9999)]))
      .toHaveLength(2);
    expect(() => buildPlaybackRetryTargets([...failures, failure('333', 0)]))
      .toThrow(/20000/);
  });

  it('counts explicit pages only after applying whole-video failures', () => {
    const failures = [...pages('111', 20001), failure('111', null), failure('222', 0)];

    expect(buildPlaybackRetryTargets(failures)).toEqual([
      { avid: '111' }, { avid: '222', pageIndexes: [0] },
    ]);
  });

  it('accepts 1000 targets but rejects a 1001st target including page chunks', () => {
    const failures = Array.from({ length: 999 }, (_, index) => failure(String(index), null));

    expect(buildPlaybackRetryTargets([...failures, failure('last', 0)]))
      .toHaveLength(1000);
    expect(() => buildPlaybackRetryTargets([...failures, ...pages('last', 10001)]))
      .toThrow(/1000/);
  });

  it('returns no targets for an empty failure list', () => {
    expect(buildPlaybackRetryTargets([])).toEqual([]);
  });
});
