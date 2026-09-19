import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppShutdown } from './app-shutdown';

describe('two-phase application shutdown', () => {
  afterEach(() => vi.useRealTimers());

  it('prevents repeated quits until cleanup completes, then allows the final quit', async () => {
    vi.useFakeTimers();
    let finish!: () => void;
    const cleanup = vi.fn(() => new Promise<void>(resolve => { finish = resolve; }));
    const error = vi.fn();
    const finalEvent = { preventDefault: vi.fn() };
    const quit = vi.fn(() => shutdown.beforeQuit(finalEvent));
    const shutdown = new AppShutdown(cleanup, quit, error);
    const first = { preventDefault: vi.fn() };
    const repeated = { preventDefault: vi.fn() };

    expect(shutdown.isShuttingDown).toBe(false);
    shutdown.beforeQuit(first);
    shutdown.beforeQuit(repeated);
    await vi.advanceTimersByTimeAsync(0);
    expect(first.preventDefault).toHaveBeenCalledOnce();
    expect(repeated.preventDefault).toHaveBeenCalledOnce();
    expect(shutdown.isShuttingDown).toBe(true);
    expect(cleanup).toHaveBeenCalledOnce();
    expect(quit).not.toHaveBeenCalled();

    finish();
    await vi.advanceTimersByTimeAsync(0);
    expect(quit).toHaveBeenCalledOnce();
    expect(finalEvent.preventDefault).not.toHaveBeenCalled();
    expect(error).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(['throw', 'reject'] as const)('still quits after a cleanup %s', async kind => {
    vi.useFakeTimers();
    const failure = new Error('cleanup failed');
    const cleanup = vi.fn(() => {
      if (kind === 'throw') throw failure;
      return Promise.reject(failure);
    });
    const quit = vi.fn();
    const error = vi.fn();
    new AppShutdown(cleanup, quit, error).beforeQuit({ preventDefault: vi.fn() });
    await vi.advanceTimersByTimeAsync(0);
    expect(error).toHaveBeenCalledWith(failure);
    expect(quit).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('bounds stalled cleanup and ignores its late completion', async () => {
    vi.useFakeTimers();
    let finish!: () => void;
    const quit = vi.fn();
    const error = vi.fn();
    const shutdown = new AppShutdown(() => new Promise<void>(resolve => { finish = resolve; }), quit, error);
    shutdown.beforeQuit({ preventDefault: vi.fn() });
    await vi.advanceTimersByTimeAsync(5_999);
    expect(quit).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(error).toHaveBeenCalledWith(expect.objectContaining({ message: expect.stringContaining('timed out') }));
    expect(quit).toHaveBeenCalledOnce();
    finish();
    await vi.advanceTimersByTimeAsync(0);
    expect(quit).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
  });
});
