import { describe, expect, it } from 'vitest';
import { HostProgressTracker } from './host-progress';

describe('monotonic request progress', () => {
  it('tracks unknown-length bootstrap bytes independently for fixed phases', () => {
    const tracker = new HostProgressTracker();
    const progress = (phase: string, bytesProcessed: number) => ({ operation: 'play', stage: phase, phase, current: 1, details: { bytesProcessed } });
    expect(tracker.advance(progress('download', 100), 'play')).toBe(true);
    expect(tracker.advance(progress('download', 100), 'play')).toBe(false);
    expect(tracker.advance(progress('verify', 0), 'play')).toBe(false);
    expect(tracker.advance(progress('verify', 10), 'play')).toBe(true);
    expect(tracker.advance(progress('extract', 20), 'play')).toBe(true);
    expect(tracker.advance(progress('download', 50), 'play')).toBe(false);
    expect(tracker.advance(progress('download', 101), 'play')).toBe(true);
  });
  it('ignores duplicates, regressions, old work items, unrelated requests and invalid values', () => {
    const tracker = new HostProgressTracker();
    const p = (current: number, bytesCopied: number, operation = 'export') => ({
      operation, phase: 'copy', stage: 'copying', current, details: { bytesCopied },
    });
    expect(tracker.advance(p(1, 100), 'export')).toBe(true);
    expect(tracker.advance(p(1, 100), 'export')).toBe(false);
    expect(tracker.advance(p(1, 50), 'export')).toBe(false);
    expect(tracker.advance(p(1, 100), 'export')).toBe(false);
    expect(tracker.advance(p(2, 1), 'export')).toBe(true);
    expect(tracker.advance(p(1, 1000), 'export')).toBe(false);
    expect(tracker.advance(p(2, 200, 'play'), 'export')).toBe(false);
    expect(tracker.advance(p(3, Number.NaN), 'export')).toBe(false);
    expect(tracker.advance(p(2, 2), 'export')).toBe(true);
  });

  it('tracks fallback independently without renewing from stage loops or waiting', () => {
    const tracker = new HostProgressTracker();
    const p = (phase: string, seconds: number) => ({
      operation: 'play', stage: phase, phase, current: 1, details: { processedSeconds: seconds },
    });
    expect(tracker.advance(p('mux', 50), 'play')).toBe(true);
    expect(tracker.advance(p('fallback', 0), 'play')).toBe(false);
    expect(tracker.advance(p('fallback', 1), 'play')).toBe(true);
    for (let i = 0; i < 4; i++) {
      expect(tracker.advance(p('mux', 50), 'play')).toBe(false);
      expect(tracker.advance(p('fallback', 1), 'play')).toBe(false);
    }
    expect(tracker.advance({ ...p('mux', 200), stage: 'waiting' }, 'play')).toBe(false);
    expect(tracker.advance({ ...p('mux', 200), phase: 'arbitrary' }, 'play')).toBe(false);
  });

  it('does not confuse index scan counters with media work item ordinals', () => {
    const tracker = new HostProgressTracker();
    expect(tracker.advance({ operation: 'play', stage: 'indexing', phase: 'scan', current: 500 }, 'play')).toBe(true);
    expect(tracker.advance({ operation: 'play', stage: 'muxing', phase: 'mux', current: 1, percentage: 1 }, 'play')).toBe(true);
    expect(tracker.advance({ operation: 'play', stage: 'muxing', phase: 'mux', current: 1, percentage: 2 }, 'play')).toBe(true);
    expect(tracker.advance({ operation: 'play', stage: 'indexing', phase: 'scan', current: 500 }, 'play')).toBe(false);
  });

  it('only accepts global numeric advancement from legacy messages', () => {
    const tracker = new HostProgressTracker();
    expect(tracker.advance({ operation: 'scan', stage: 'scanning', details: { processedSegmentDirectories: 1 } }, 'scan')).toBe(true);
    expect(tracker.advance({ operation: 'scan', stage: 'other', percentage: 50, details: { elapsedMilliseconds: 100 } }, 'scan')).toBe(false);
    expect(tracker.advance({ operation: 'scan', stage: 'scanning', details: { processedSegmentDirectories: 2 } }, 'scan')).toBe(true);
  });
});
