import { describe, expect, it, vi } from 'vitest';

vi.mock('electron', () => ({
  app: {
    getAppPath: () => '',
    isPackaged: false,
  },
}));

import { createHostEnvironment } from './host-bridge';

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
