import { EventEmitter } from 'node:events';
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

import { createHostEnvironment, DesktopHostBridge } from './host-bridge';

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

  it('rejects locally and sends Host cancel for an active call', async () => {
    const fake = createFakeHostProcess();
    hostMocks.spawn.mockReturnValue(fake.child);
    const bridge = new DesktopHostBridge();

    const call = bridge.call('export', { outputPath: 'C:\\export.mp4' });
    await vi.waitFor(() => expect(fake.writes).toHaveLength(1));
    expect(call.cancel()).toBe(true);
    expect(call.cancel()).toBe(false);
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
    stdin: EventEmitter & { write: ReturnType<typeof vi.fn>; end: ReturnType<typeof vi.fn> };
    stdout: EventEmitter & { setEncoding: ReturnType<typeof vi.fn> };
    stderr: EventEmitter & { setEncoding: ReturnType<typeof vi.fn> };
    kill: ReturnType<typeof vi.fn>;
  };
  child.killed = false;
  child.stdout = Object.assign(new EventEmitter(), { setEncoding: vi.fn() });
  child.stderr = Object.assign(new EventEmitter(), { setEncoding: vi.fn() });
  child.stdin = Object.assign(new EventEmitter(), {
    write: vi.fn((chunk: string, _encoding: string, callback: (error?: Error | null) => void) => {
      writes.push(JSON.parse(chunk.trim()) as { id: string; method: string; params: Record<string, unknown> });
      callback(null);
      return true;
    }),
    end: vi.fn(() => {
      queueMicrotask(() => child.emit('exit', 0, null));
    }),
  });
  child.kill = vi.fn(() => {
    child.killed = true;
    queueMicrotask(() => child.emit('exit', null, 'SIGTERM'));
    return true;
  });
  return child;
}
