import { EventEmitter } from 'node:events';
import { Writable } from 'node:stream';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const hostMocks = vi.hoisted(() => ({
  existsSync: vi.fn(() => true),
  spawn: vi.fn(),
}));

vi.mock('electron', () => ({
  app: {
    getAppPath: () => '',
    isPackaged: false,
  },
}));

vi.mock('node:child_process', () => ({ spawn: hostMocks.spawn }));
vi.mock('node:fs', () => ({ existsSync: hostMocks.existsSync }));

import { createHostEnvironment, DesktopHostBridge, hostTimeoutPolicy } from './host-bridge';

const originalHostPath = process.env.CACHE_MANAGER_HOST_PATH;

beforeEach(() => {
  hostMocks.existsSync.mockReturnValue(true);
  hostMocks.spawn.mockReset();
  process.env.CACHE_MANAGER_HOST_PATH = 'C:\\test\\Desktop.Host.dll';
});

afterEach(() => {
  if (originalHostPath === undefined) delete process.env.CACHE_MANAGER_HOST_PATH;
  else process.env.CACHE_MANAGER_HOST_PATH = originalHostPath;
});

describe('Desktop Host environment', () => {
  const dangerousOverrides = {
    CACHE_MANAGER_HOST_PATH: '/poison/host',
    CACHE_MANAGER_DOTNET_PATH: '/poison/dotnet',
    BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: '/poison/settings.json',
    BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: '/poison/transcode',
    BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH: '/poison/ffmpeg.zip',
    BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_DOWNLOAD_URL: 'https://poison.invalid/ffmpeg.zip',
    BILIBILI_LOCAL_CACHE_MANAGER_USE_SYSTEM_FFMPEG: '1',
    BILIBILI_RUN_FFMPEG_INTEGRATION_TESTS: '1',
    FFMPEG_BUNDLE_TAG: 'poison-tag',
    FFMPEG_BUNDLE_ASSET: 'poison.zip',
    FFMPEG_BUNDLE_SHA256: '0'.repeat(64),
    DOTNET_STARTUP_HOOKS: '/poison/startup-hook.dll',
    DOTNET_GCPath: '/poison/gc.dll',
    CORECLR_ENABLE_PROFILING: '1',
    CORECLR_PROFILER_PATH_64: '/poison/profiler.dll',
    COMPlus_ProfAPI_ProfilerCompatibilitySetting: 'EnableV2Profiler',
    COR_ENABLE_PROFILING: '1',
  } satisfies NodeJS.ProcessEnv;

  it('removes every development and test override from packaged launches', () => {
    const source: NodeJS.ProcessEnv = {
      ...dangerousOverrides,
      Path: '/system/bin',
      DISPLAY: ':0',
      XDG_CURRENT_DESKTOP: 'GNOME',
    };

    const environment = createHostEnvironment(source, true);

    expect(environment).toEqual({
      Path: '/system/bin',
      DISPLAY: ':0',
      XDG_CURRENT_DESKTOP: 'GNOME',
    });
    expect(source).toMatchObject(dangerousOverrides);
  });

  it('matches override names case-insensitively for Windows environments', () => {
    const environment = createHostEnvironment({
      cache_manager_host_path: 'C:\\poison\\host.exe',
      bilibili_local_cache_manager_ffmpeg_archive_path: 'C:\\poison\\ffmpeg.zip',
      dotnet_startup_hooks: 'C:\\poison\\startup-hook.dll',
      CoreClr_Profiler_Path: 'C:\\poison\\profiler.dll',
      cor_enable_profiling: '1',
      SystemRoot: 'C:\\Windows',
    }, true);

    expect(environment).toEqual({ SystemRoot: 'C:\\Windows' });
  });

  it('preserves overrides for explicit development launches', () => {
    const source: NodeJS.ProcessEnv = {
      ...dangerousOverrides,
      PATH: '/usr/bin',
    };

    const environment = createHostEnvironment(source, false);

    expect(environment).toEqual(source);
    expect(environment).not.toBe(source);
  });

  it('replaces inherited packaged smoke paths with main-process trusted paths', () => {
    const environment = createHostEnvironment({
      BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: 'C:\\poison\\settings.json',
      BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: 'C:\\poison\\transcode',
      BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH: 'C:\\poison\\ffmpeg.zip',
      SystemRoot: 'C:\\Windows',
    }, true, {
      BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: 'C:\\safe-smoke\\settings.json',
      BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: 'C:\\safe-smoke\\transcode',
    });

    expect(environment).toEqual({
      BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: 'C:\\safe-smoke\\settings.json',
      BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: 'C:\\safe-smoke\\transcode',
      SystemRoot: 'C:\\Windows',
    });
  });

  it('rejects trusted overrides outside the smoke settings allowlist', () => {
    expect(() => createHostEnvironment({}, true, {
      BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH: 'C:\\poison\\ffmpeg.zip',
    })).toThrow(/不允许向 Desktop Host 注入可信环境变量/);
  });
});

describe('Desktop Host cancellation', () => {
  it('uses short query deadlines and progress-based media deadlines', () => {
    expect(hostTimeoutPolicy('health')).toEqual({ milliseconds: 15_000, idle: false });
    expect(hostTimeoutPolicy('search')).toEqual({ milliseconds: 60_000, idle: false });
    expect(hostTimeoutPolicy('export')).toEqual({ milliseconds: 600_000, idle: true });
  });

  it('keeps an advancing export alive beyond ten minutes, then cancels a stalled export', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    try {
      const call = bridge.call('export');
      const rejection = expect(call.promise).rejects.toMatchObject({ code: 'cancelled' });
      await vi.advanceTimersByTimeAsync(0);
      const progress = (bytesCopied: number, requestId = call.id, operation = 'export') =>
        fake.child.stdout.emit('data', JSON.stringify({ event: 'progress', payload: {
          requestId, operation, stage: 'copying', phase: 'copy', details: { bytesCopied },
        } }) + '\n');
      await vi.advanceTimersByTimeAsync(500_000);
      progress(100);
      await vi.advanceTimersByTimeAsync(500_000);
      progress(200);
      expect(fake.writes.filter(value => value.method === 'cancel')).toHaveLength(0);
      await vi.advanceTimersByTimeAsync(500_000);
      progress(200);
      progress(300, 'another-request');
      progress(400, call.id, 'scan');
      await vi.advanceTimersByTimeAsync(100_001);
      fake.child.stdout.emit('data', JSON.stringify({ id: call.id, error: { code: 'cancelled', message: 'Cancelled' } }) + '\n');
      await rejection;
      expect(fake.writes.filter(value => value.method === 'cancel')).toHaveLength(1);
      await bridge.dispose();
    } finally { vi.useRealTimers(); }
  });

  it('does not renew a timeout from repeated lock waiting or elapsed time', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    try {
      const call = bridge.call('play');
      const rejection = expect(call.promise).rejects.toMatchObject({ code: 'cancelled' });
      await vi.advanceTimersByTimeAsync(0);
      await vi.advanceTimersByTimeAsync(500_000);
      fake.child.stdout.emit('data', JSON.stringify({ event: 'progress', payload: {
        requestId: call.id, operation: 'play', stage: 'waiting for lock', details: { elapsedMilliseconds: 500_000 },
      } }) + '\n');
      await vi.advanceTimersByTimeAsync(100_001);
      expect(fake.writes.filter(value => value.method === 'cancel')).toHaveLength(1);
      fake.child.stdout.emit('data', JSON.stringify({ id: call.id, error: { code: 'cancelled', message: 'Cancelled' } }) + '\n');
      await rejection;
      await bridge.dispose();
    } finally { vi.useRealTimers(); }
  });
  it('sends a Host cancel request before rejecting a timed-out call', async () => {
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();

    const call = bridge.call('scan', { rootPath: 'C:\\cache' }, 10);
    const rejected = expect(call.promise).rejects.toMatchObject({ code: 'HOST_TIMEOUT' });
    await vi.waitFor(() => expect(fake.writes[0]).toMatchObject({ method: 'scan' }));
    await rejected;

    expect(fake.writes).toHaveLength(2);
    expect(fake.writes[1]).toMatchObject({
      method: 'cancel',
      params: { requestId: call.id },
    });
    await bridge.dispose();
  });

  it('waits for the Host terminal response after requesting cancellation', async () => {
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();

    const call = bridge.call('export', { outputPath: 'C:\\export.mp4' });
    await vi.waitFor(() => expect(fake.writes).toHaveLength(1));
    expect(call.cancel()).toBe(true);
    expect(call.cancel()).toBe(false);
    fake.child.stdout.emit('data', JSON.stringify({ id: call.id, error: { code: 'CANCELLED', message: 'Cancelled' } }) + '\n');
    await expect(call.promise).rejects.toMatchObject({ code: 'CANCELLED' });

    expect(fake.writes[1]).toMatchObject({
      method: 'cancel',
      params: { requestId: call.id },
    });
    await bridge.dispose();
  });

  it('reports synchronous Host startup failures as unavailable', async () => {
    hostMocks.existsSync.mockReturnValue(false);
    const bridge = new DesktopHostBridge();
    const unavailable = vi.fn();
    bridge.on('unavailable', unavailable);

    await expect(bridge.call('health').promise).rejects.toMatchObject({ code: 'HOST_NOT_FOUND' });

    expect(unavailable).toHaveBeenCalledOnce();
    expect(unavailable).toHaveBeenCalledWith(expect.stringContaining('找不到 .NET Desktop Host'));
  });
});

describe('Host lifecycle and confirmed cancellation', () => {
  afterEach(() => vi.useRealTimers());

  it('waits beyond the confirmation deadline and accepts a late successful commit', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const state = vi.fn();
    bridge.on('operation-state', state);
    const call = bridge.call('export');
    const settled = vi.fn();
    call.promise.then(settled, settled);
    await vi.advanceTimersByTimeAsync(0);
    expect(call.cancel()).toBe(true);
    expect(call.cancel()).toBe(false);
    await vi.advanceTimersByTimeAsync(29_000);
    fake.child.stdout.emit('data', JSON.stringify({ event: 'progress', payload: {
      requestId: call.id, operation: 'export', stage: 'copying', phase: 'copy', current: 1, details: { bytesCopied: 100 },
    } }) + '\n');
    await vi.advanceTimersByTimeAsync(1000);
    expect(state).toHaveBeenLastCalledWith(expect.objectContaining({ state: 'unconfirmed', requestId: call.id }));
    expect(settled).not.toHaveBeenCalled();
    expect(fake.child.kill).not.toHaveBeenCalled();
    await expect(bridge.call('trash.move').promise).rejects.toMatchObject({ code: 'OUTCOME_UNCONFIRMED' });
    fake.child.stdout.emit('data', JSON.stringify({ id: call.id, result: { published: true } }) + '\n');
    await expect(call.promise).resolves.toEqual({ published: true });
    expect(state).toHaveBeenLastCalledWith(expect.objectContaining({ state: 'settled' }));
    expect(vi.getTimerCount()).toBe(0);
    await bridge.dispose();
  });

  it('requests cancellation on a media timeout without declaring an uncommitted outcome', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const state = vi.fn();
    bridge.on('operation-state', state);
    const call = bridge.call('play', {}, 10);
    await vi.advanceTimersByTimeAsync(30_010);
    expect(state).toHaveBeenLastCalledWith(expect.objectContaining({ state: 'unconfirmed' }));
    const rejected = expect(call.promise).rejects.toMatchObject({ code: 'OUTCOME_UNKNOWN' });
    fake.child.emit('exit', 1, null);
    fake.child.emit('close', 1, null);
    await rejected;
    expect(state).toHaveBeenLastCalledWith(expect.objectContaining({ state: 'unknown' }));
    await expect(bridge.call('play').promise).rejects.toMatchObject({ code: 'OUTCOME_UNCONFIRMED' });
    expect(bridge.acknowledgeUncertain('unrelated')).toBe(false);
    expect(bridge.acknowledgeUncertain(call.id)).toBe(true);
    expect(bridge.acknowledgeUncertain(call.id)).toBe(false);
    expect(vi.getTimerCount()).toBe(0);
    await bridge.dispose();
  });

  it('isolates delayed data, error, exit and write callbacks across repeated restarts', async () => {
    vi.useFakeTimers();
    const bridge = new DesktopHostBridge();
    const unavailable = vi.fn();
    bridge.on('unavailable', unavailable);
    let previous: ReturnType<typeof createFakeHostProcess> | undefined;
    let callbacks: { data: Function; error: Function; write: (error?: Error | null) => void } | undefined;
    for (let i = 0; i < 3; i++) {
      const fake = createFakeHostProcess();
      fake.child.kill.mockImplementation(() => { fake.child.killed = true; return true; });
      hostMocks.spawn.mockReturnValue(fake.child);
      const call = bridge.call('search');
      await vi.advanceTimersByTimeAsync(0);
      if (previous && callbacks) {
        callbacks.data(JSON.stringify({ id: call.id, result: 'poison' }) + '\n');
        callbacks.error(new Error('late error'));
        callbacks.write(new Error('late write'));
        previous.child.emit('exit', 1, null);
        previous.child.emit('close', 1, null);
        expect(previous.child.stdout.listenerCount('data')).toBe(0);
        expect(previous.child.listenerCount('error')).toBe(0);
      }
      callbacks = { data: fake.child.stdout.listeners('data')[0], error: fake.child.listeners('error')[0],
        write: fake.child.stdin.write.mock.calls[0][2] };
      if (i < 2) {
        const rejection = expect(call.promise).rejects.toMatchObject({ code: 'HOST_PROTOCOL_ERROR' });
        fake.child.stdout.emit('data', 'invalid json\n');
        await rejection;
      } else {
        fake.child.stdout.emit('data', JSON.stringify({ id: call.id, result: 'healthy' }) + '\n');
        await expect(call.promise).resolves.toBe('healthy');
      }
      previous = fake;
    }
    expect(unavailable).toHaveBeenCalledTimes(2);
    await bridge.dispose();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(['request', 'cancel'] as const)('handles a real Writable %s callback failure and its later stream error once', async failure => {
    const fake = createFakeHostProcess();
    const input = new Writable({
      write(chunk, _encoding, callback) {
        const request = JSON.parse(chunk.toString()) as { method: string };
        callback((request.method === 'cancel') === (failure === 'cancel') ? new Error('broken input') : null);
      },
    });
    Object.assign(fake.child, { stdin: input });
    const inputClosed = new Promise<void>(resolve => input.once('close', resolve));
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const unavailable = vi.fn();
    bridge.on('unavailable', unavailable);
    const call = bridge.call(failure === 'cancel' ? 'export' : 'search');
    const result = call.promise.catch(error => error.code);
    if (failure === 'cancel') {
      await vi.waitFor(() => expect(input.listenerCount('error')).toBe(1));
      call.cancel();
    }
    await inputClosed;
    expect(await result).toBe(failure === 'cancel' ? 'OUTCOME_UNKNOWN' : 'HOST_WRITE_FAILED');
    expect(unavailable).toHaveBeenCalledOnce();
    expect(fake.child.kill).toHaveBeenCalledOnce();
    expect(input.listenerCount('error')).toBe(0);
    expect(input.listenerCount('close')).toBe(0);
    await bridge.dispose();
  });

  it('handles a stream-only input failure without treating side effects as cancelled', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const state = vi.fn();
    const unavailable = vi.fn();
    bridge.on('operation-state', state);
    bridge.on('unavailable', unavailable);
    const call = bridge.call('export');
    const result = call.promise.catch(error => error.code);
    await vi.advanceTimersByTimeAsync(0);
    expect(() => fake.child.stdin.emit('error', new Error('pipe disconnected'))).not.toThrow();
    expect(await result).toBe('OUTCOME_UNKNOWN');
    expect(state).toHaveBeenLastCalledWith(expect.objectContaining({ state: 'unknown' }));
    expect(unavailable).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(0);
    fake.child.stdin.emit('close');
    expect(fake.child.stdin.listenerCount('error')).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
    await bridge.dispose();
  });

  it('absorbs detached old input errors until input close without affecting a new Host', async () => {
    vi.useFakeTimers();
    const old = createFakeHostProcess();
    const next = createFakeHostProcess();
    hostMocks.spawn.mockReturnValueOnce(old.child).mockReturnValueOnce(next.child);
    const bridge = new DesktopHostBridge();
    const unavailable = vi.fn();
    bridge.on('unavailable', unavailable);
    const first = bridge.call('search');
    const firstResult = first.promise.catch(error => error.code);
    await vi.advanceTimersByTimeAsync(0);
    old.child.stdin.write.mock.calls[0][2](new Error('write failed'));
    await vi.advanceTimersByTimeAsync(0);
    expect(await firstResult).toBe('HOST_WRITE_FAILED');
    expect(old.child.stdin.listenerCount('error')).toBe(1);
    const second = bridge.call('health');
    await vi.advanceTimersByTimeAsync(0);
    expect(() => old.child.stdin.emit('error', new Error('late stream error'))).not.toThrow();
    old.child.stdin.emit('close');
    expect(old.child.stdin.listenerCount('error')).toBe(0);
    expect(unavailable).toHaveBeenCalledOnce();
    next.child.stdout.emit('data', JSON.stringify({ id: second.id, result: 'healthy' }) + '\n');
    await expect(second.promise).resolves.toBe('healthy');
    await bridge.dispose();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('guards a queued destroy error after shutdown detaches the child', async () => {
    const fake = createFakeHostProcess();
    const input = new Writable({
      write(_chunk, _encoding, callback) { callback(); },
      final(callback) { callback(); fake.child.emit('exit', 0, null); fake.child.emit('close', 0, null); },
      destroy(_error, callback) { queueMicrotask(() => callback(new Error('late destroy failure'))); },
    });
    Object.assign(fake.child, { stdin: input });
    const inputClosed = new Promise<void>(resolve => input.once('close', resolve));
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const unavailable = vi.fn();
    bridge.on('unavailable', unavailable);
    const call = bridge.call('search');
    const result = call.promise.catch(error => error.code);
    await vi.waitFor(() => expect(input.listenerCount('error')).toBe(1));
    await bridge.dispose();
    await inputClosed;
    expect(await result).toBe('APP_CLOSING');
    expect(unavailable).not.toHaveBeenCalled();
    expect(input.listenerCount('error')).toBe(0);
    expect(input.listenerCount('close')).toBe(0);
  });

  it('does not spawn a process when disposal races initial startup', async () => {
    const bridge = new DesktopHostBridge();
    const call = bridge.call('health');
    const rejected = expect(call.promise).rejects.toMatchObject({ code: 'APP_CLOSING' });
    await bridge.dispose();
    await rejected;
    expect(hostMocks.spawn).not.toHaveBeenCalled();
    await expect(bridge.call('health').promise).rejects.toMatchObject({ code: 'APP_CLOSING' });
  });

  it('shares shutdown across repeated disposals and waits for graceful close', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    fake.child.stdin.end.mockImplementation(() => {});
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const call = bridge.call('search');
    const result = call.promise.catch(error => error.code);
    await vi.advanceTimersByTimeAsync(0);
    const disposal = bridge.dispose();
    expect(bridge.dispose()).toBe(disposal);
    const done = vi.fn();
    void disposal.then(done);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(done).not.toHaveBeenCalled();
    expect(fake.child.stdin.end).toHaveBeenCalledOnce();
    expect(await result).toBe('APP_CLOSING');
    fake.child.emit('exit', 0, null);
    fake.child.emit('close', 0, null);
    await disposal;
    expect(fake.child.kill).not.toHaveBeenCalled();
    expect(fake.child.stdin.listenerCount('error')).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('force kills only on shutdown when the graceful deadline expires', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    fake.child.stdin.end.mockImplementation(() => {});
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const call = bridge.call('play');
    void call.promise.catch(() => {});
    await vi.advanceTimersByTimeAsync(0);
    const disposal = bridge.dispose();
    await vi.advanceTimersByTimeAsync(1_499);
    expect(fake.child.kill).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    await disposal;
    expect(fake.child.kill).toHaveBeenCalledExactlyOnceWith('SIGKILL');
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(['false', 'throw', 'no-close'] as const)('bounds a %s kill and releases handles', async failure => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    fake.child.stdin.end.mockImplementation(() => { throw new Error('closed input'); });
    fake.child.kill.mockImplementation(() => {
      if (failure === 'throw') throw new Error('access denied');
      if (failure === 'no-close') fake.child.emit('exit', null, 'SIGKILL');
      return failure !== 'false';
    });
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const call = bridge.call('search');
    void call.promise.catch(() => {});
    await vi.advanceTimersByTimeAsync(0);
    const disposal = bridge.dispose();
    const result = disposal.catch(error => error.code);
    await vi.advanceTimersByTimeAsync(4_999);
    const done = vi.fn();
    void result.then(done);
    await vi.advanceTimersByTimeAsync(0);
    expect(done).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(await result).toBe('HOST_SHUTDOWN_TIMEOUT');
    expect(bridge.dispose()).toBe(disposal);
    expect(fake.child.kill).toHaveBeenCalledOnce();
    expect(fake.child.stdin.destroy).toHaveBeenCalledOnce();
    expect(fake.child.stdout.destroy).toHaveBeenCalledOnce();
    expect(fake.child.stderr.destroy).toHaveBeenCalledOnce();
    expect(fake.child.unref).toHaveBeenCalledOnce();
    expect(fake.child.stdin.listenerCount('error')).toBe(0);
    expect(fake.child.stdout.listenerCount('data')).toBe(0);
    expect(fake.child.listenerCount('exit')).toBe(0);
    expect(fake.child.listenerCount('close')).toBe(0);
    expect(fake.child.listenerCount('error')).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
    await expect(bridge.call('health').promise).rejects.toMatchObject({ code: 'APP_CLOSING' });
  });

  it('releases calls waiting for output drain when shutdown reaches its deadline', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const first = bridge.call('search');
    void first.promise.catch(() => {});
    await vi.advanceTimersByTimeAsync(0);
    fake.child.emit('exit', 0, null);
    const waiting = bridge.call('health');
    const waitingResult = waiting.promise.catch(error => error.code);
    const disposal = bridge.dispose();
    await vi.advanceTimersByTimeAsync(5_000);
    await disposal;
    expect(await waitingResult).toBe('APP_CLOSING');
    expect(hostMocks.spawn).toHaveBeenCalledOnce();
    expect(fake.child.kill).not.toHaveBeenCalled();
    expect(fake.child.stdout.destroy).toHaveBeenCalledOnce();
    expect(fake.child.stderr.destroy).toHaveBeenCalledOnce();
    expect(fake.child.unref).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(['success', 'cancelled', 'error'] as const)('drains a split terminal %s response after exit', async outcome => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const state = vi.fn();
    bridge.on('operation-state', state);
    const call = bridge.call('export');
    const result = call.promise.then(value => value, error => error.code);
    await vi.advanceTimersByTimeAsync(0);
    call.cancel();
    const message = JSON.stringify(outcome === 'success'
      ? { id: call.id, result: { published: true } }
      : { id: call.id, error: { code: outcome === 'cancelled' ? 'CANCELLED' : 'EXPORT_FAILED', message: outcome } }) + '\n';
    fake.child.stdout.emit('data', message.slice(0, 20));
    fake.child.emit('exit', 0, null);
    expect(fake.child.stdout.listenerCount('data')).toBe(1);
    // A late write failure must not discard the terminal response either.
    fake.child.stdin.write.mock.calls[0][2](new Error('closed pipe'));
    expect(() => fake.child.stdin.emit('error', new Error('late input error'))).not.toThrow();
    fake.child.stdout.emit('data', message.slice(20));
    fake.child.emit('close', 0, null);
    fake.child.stdin.emit('close');
    expect(await result).toEqual(outcome === 'success' ? { published: true } : outcome === 'cancelled' ? 'CANCELLED' : 'EXPORT_FAILED');
    expect(state).toHaveBeenLastCalledWith(expect.objectContaining({ state: 'settled' }));
    expect(state.mock.calls.some(([value]) => value.state === 'unknown')).toBe(false);
    expect(fake.child.stdout.listenerCount('data')).toBe(0);
    expect(fake.child.listenerCount('error')).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
    await bridge.dispose();
  });

  it('flushes a final response without a newline only when output is closed', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const call = bridge.call('export');
    const settled = vi.fn();
    void call.promise.then(settled);
    await vi.advanceTimersByTimeAsync(0);
    fake.child.emit('exit', 0, null);
    fake.child.stdout.emit('data', JSON.stringify({ id: call.id, result: { published: true } }));
    await vi.advanceTimersByTimeAsync(0);
    expect(settled).not.toHaveBeenCalled();
    fake.child.emit('close', 0, null);
    await expect(call.promise).resolves.toEqual({ published: true });
    expect(vi.getTimerCount()).toBe(0);
    await bridge.dispose();
  });

  it('waits for output closure before restarting and preserves the old final response', async () => {
    vi.useFakeTimers();
    const old = createFakeHostProcess();
    const next = createFakeHostProcess();
    hostMocks.spawn.mockReturnValueOnce(old.child).mockReturnValueOnce(next.child);
    const bridge = new DesktopHostBridge();
    const first = bridge.call('search');
    await vi.advanceTimersByTimeAsync(0);
    old.child.emit('exit', 0, null);
    const second = bridge.call('health');
    await vi.advanceTimersByTimeAsync(0);
    expect(hostMocks.spawn).toHaveBeenCalledTimes(1);
    expect(old.writes).toHaveLength(1);
    old.child.stdout.emit('data', JSON.stringify({ id: first.id, result: 'old result' }) + '\n');
    old.child.emit('close', 0, null);
    await expect(first.promise).resolves.toBe('old result');
    await vi.advanceTimersByTimeAsync(0);
    expect(hostMocks.spawn).toHaveBeenCalledTimes(2);
    next.child.stdout.emit('data', JSON.stringify({ id: second.id, result: 'new result' }) + '\n');
    await expect(second.promise).resolves.toBe('new result');
    await bridge.dispose();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('bounds output draining and marks only unanswered side effects unknown', async () => {
    vi.useFakeTimers();
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();
    const call = bridge.call('export');
    const result = call.promise.catch(error => error.code);
    await vi.advanceTimersByTimeAsync(0);
    fake.child.emit('exit', 1, null);
    fake.child.stdout.emit('data', '{"id":');
    const settled = vi.fn();
    void result.then(settled);
    await vi.advanceTimersByTimeAsync(4_999);
    expect(settled).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(await result).toBe('OUTCOME_UNKNOWN');
    expect(fake.child.stdout.listenerCount('data')).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
    expect(fake.child.kill).not.toHaveBeenCalled();
    await bridge.dispose();
  });
});

function createFakeHostProcess(): {
  child: ReturnType<typeof createFakeChild>;
  writes: Array<{ id: string; method: string; params: Record<string, unknown> }>;
} {
  const writes: Array<{ id: string; method: string; params: Record<string, unknown> }> = [];
  const child = createFakeChild(writes);
  return { child, writes };
}

function createFakeChild(writes: Array<{ id: string; method: string; params: Record<string, unknown> }>) {
  const child = new EventEmitter() as EventEmitter & {
    killed: boolean;
    stdin: EventEmitter & { write: ReturnType<typeof vi.fn>; end: ReturnType<typeof vi.fn>; destroy: ReturnType<typeof vi.fn> };
    stdout: EventEmitter & { setEncoding: ReturnType<typeof vi.fn>; destroy: ReturnType<typeof vi.fn> };
    stderr: EventEmitter & { setEncoding: ReturnType<typeof vi.fn>; destroy: ReturnType<typeof vi.fn> };
    kill: ReturnType<typeof vi.fn>;
    unref: ReturnType<typeof vi.fn>;
  };
  child.killed = false;
  child.stdout = Object.assign(new EventEmitter(), { setEncoding: vi.fn(), destroy: vi.fn() });
  child.stderr = Object.assign(new EventEmitter(), { setEncoding: vi.fn(), destroy: vi.fn() });
  child.unref = vi.fn();
  child.stdin = Object.assign(new EventEmitter(), {
    destroy: vi.fn(() => { child.stdin.emit('close'); }),
    write: vi.fn((chunk: string, _encoding: string, callback: (error?: Error | null) => void) => {
      writes.push(JSON.parse(chunk.trim()) as { id: string; method: string; params: Record<string, unknown> });
      callback(null);
      return true;
    }),
    end: vi.fn(() => {
      queueMicrotask(() => { child.emit('exit', 0, null); child.emit('close', 0, null); });
    }),
  });
  child.kill = vi.fn(() => {
    child.killed = true;
    queueMicrotask(() => { child.emit('exit', null, 'SIGTERM'); child.emit('close', null, 'SIGTERM'); });
    return true;
  });
  return child;
}
