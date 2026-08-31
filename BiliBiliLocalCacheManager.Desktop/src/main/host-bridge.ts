import { app } from 'electron';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { existsSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import type { JsonObject, JsonValue } from '../shared/contracts';
import { JsonLineDecoder, type HostMessage } from './protocol';

interface PendingRequest {
  resolve(value: JsonValue): void;
  reject(error: Error): void;
  timer: NodeJS.Timeout;
}

const MAX_HOST_REQUEST_BYTES = 1024 * 1024;

const unsafePackagedHostEnvironmentVariables = new Set([
  'CACHE_MANAGER_HOST_PATH',
  'CACHE_MANAGER_DOTNET_PATH',
  'BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH',
  'BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT',
  'BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH',
  'BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_DOWNLOAD_URL',
  'BILIBILI_LOCAL_CACHE_MANAGER_USE_SYSTEM_FFMPEG',
  'BILIBILI_RUN_FFMPEG_INTEGRATION_TESTS',
  'FFMPEG_BUNDLE_TAG',
  'FFMPEG_BUNDLE_ASSET',
  'FFMPEG_BUNDLE_SHA256',
]);

// A packaged self-contained Host must not inherit runtime injection knobs from
// the shell that launched Electron. These prefixes cover startup hooks, custom
// GC/profiler libraries, additional dependency stores, diagnostic ports, and
// single-file extraction overrides. The bridge adds back only its own benign
// DOTNET_NOLOGO and telemetry settings at the spawn call site.
const unsafePackagedHostEnvironmentPrefixes = [
  'DOTNET_',
  'CORECLR_',
  'COMPLUS_',
  'COR_',
];

const trustedHostEnvironmentOverrideVariables = new Set([
  'BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH',
  'BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT',
]);

export function createHostEnvironment(
  source: NodeJS.ProcessEnv,
  isPackaged: boolean,
  trustedOverrides: NodeJS.ProcessEnv = {},
): NodeJS.ProcessEnv {
  const environment = { ...source };
  if (isPackaged) {
    for (const name of Object.keys(environment)) {
      const normalizedName = name.toUpperCase();
      if (unsafePackagedHostEnvironmentVariables.has(normalizedName) ||
          unsafePackagedHostEnvironmentPrefixes.some((prefix) => normalizedName.startsWith(prefix))) {
        delete environment[name];
      }
    }
  }

  for (const [name, value] of Object.entries(trustedOverrides)) {
    const normalizedName = name.toUpperCase();
    if (!trustedHostEnvironmentOverrideVariables.has(normalizedName)) {
      throw new Error(`不允许向 Desktop Host 注入可信环境变量：${name}`);
    }
    if (value !== undefined) environment[normalizedName] = value;
  }
  return environment;
}

export interface DesktopHostBridgeOptions {
  trustedEnvOverrides?: NodeJS.ProcessEnv;
}

export interface HostCall<T> {
  id: string;
  promise: Promise<T>;
  cancel(): boolean;
}

export class DesktopHostError extends Error {
  constructor(
    message: string,
    readonly code = 'HOST_ERROR',
    readonly details?: JsonValue,
  ) {
    super(message);
    this.name = 'DesktopHostError';
  }
}

export class DesktopHostBridge extends EventEmitter {
  #process: ChildProcessWithoutNullStreams | null = null;
  #decoder = new JsonLineDecoder();
  #pending = new Map<string, PendingRequest>();
  #starting: Promise<void> | null = null;
  #stopping = false;
  #stderrTail = '';
  readonly #trustedEnvOverrides: NodeJS.ProcessEnv;

  constructor(options: DesktopHostBridgeOptions = {}) {
    super();
    this.#trustedEnvOverrides = { ...(options.trustedEnvOverrides ?? {}) };
  }

  call<T>(method: string, params: JsonObject = {}, timeoutMs = 10 * 60_000): HostCall<T> {
    const id = randomUUID();
    let settled = false;
    let cancelRequested = false;
    let request: string;
    try {
      request = JSON.stringify({ id, method, params });
    } catch (error) {
      return {
        id,
        promise: Promise.reject(new DesktopHostError(
          `无法序列化 Desktop Host 请求：${error instanceof Error ? error.message : String(error)}`,
          'INVALID_REQUEST',
        )),
        cancel: () => false,
      };
    }
    if (Buffer.byteLength(request, 'utf8') > MAX_HOST_REQUEST_BYTES) {
      return {
        id,
        promise: Promise.reject(new DesktopHostError(
          'Desktop Host 请求超过 1 MiB 安全上限。请减少一次操作中的项目或分段数量。',
          'REQUEST_TOO_LARGE',
        )),
        cancel: () => false,
      };
    }
    const promise = this.#start().then(
      () => {
        if (cancelRequested) {
          settled = true;
          throw new DesktopHostError('操作已取消。', 'CANCELLED');
        }
        return new Promise<T>((resolve, reject) => {
          const timer = setTimeout(() => {
            this.#pending.delete(id);
            settled = true;
            this.#sendCancellation(id);
            reject(new DesktopHostError(`Desktop Host 调用超时：${method}`, 'HOST_TIMEOUT'));
          }, timeoutMs);
          this.#pending.set(id, {
            resolve: (value) => {
              settled = true;
              resolve(value as T);
            },
            reject: (error) => {
              settled = true;
              reject(error);
            },
            timer,
          });
          this.#process!.stdin.write(`${request}\n`, 'utf8', (error) => {
            if (!error) return;
            const pending = this.#pending.get(id);
            if (!pending) return;
            clearTimeout(pending.timer);
            this.#pending.delete(id);
            const transportError = new DesktopHostError(`无法向 Desktop Host 写入请求：${error.message}`, 'HOST_WRITE_FAILED');
            pending.reject(transportError);
            this.#process?.kill();
            this.#handleExit(transportError);
          });
        });
      },
    );
    promise.then(
      () => { settled = true; },
      () => { settled = true; },
    );
    return {
      id,
      promise,
      cancel: () => {
        if (settled || cancelRequested) return false;
        cancelRequested = true;
        const pending = this.#pending.get(id);
        if (!pending) return true;
        clearTimeout(pending.timer);
        this.#pending.delete(id);
        this.#sendCancellation(id);
        pending.reject(new DesktopHostError('操作已取消。', 'CANCELLED'));
        return true;
      },
    };
  }

  async dispose(): Promise<void> {
    this.#stopping = true;
    const process = this.#process;
    this.#process = null;
    this.#starting = null;
    this.#rejectPending(new DesktopHostError('桌面应用正在关闭。', 'APP_CLOSING'));
    if (!process || process.killed) return;
    process.stdin.end();
    const exited = new Promise<void>((resolve) => process.once('exit', () => resolve()));
    const force = setTimeout(() => process.kill(), 1_500);
    await exited;
    clearTimeout(force);
  }

  async #start(): Promise<void> {
    if (this.#process && !this.#process.killed) return;
    if (this.#starting) return this.#starting;
    this.#starting = this.#spawnHost()
      .catch((error: unknown) => {
        const normalized = error instanceof DesktopHostError
          ? error
          : new DesktopHostError(
              error instanceof Error ? error.message : String(error),
              'HOST_START_FAILED',
            );
        this.#handleExit(normalized);
        throw normalized;
      })
      .finally(() => {
        this.#starting = null;
      });
    return this.#starting;
  }

  async #spawnHost(): Promise<void> {
    if (process.arch !== 'x64' || (process.platform !== 'win32' && process.platform !== 'linux')) {
      throw new DesktopHostError(`当前版本不支持 ${process.platform}/${process.arch}。`, 'UNSUPPORTED_PLATFORM');
    }

    const launch = resolveHostLaunch();
    this.#decoder = new JsonLineDecoder();
    this.#stderrTail = '';
    this.#stopping = false;
    const child = spawn(launch.command, launch.args, {
      cwd: path.dirname(launch.hostPath),
      env: {
        ...createHostEnvironment(
          process.env,
          app.isPackaged,
          this.#trustedEnvOverrides,
        ),
        DOTNET_NOLOGO: '1',
        DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      },
      shell: false,
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    this.#process = child;
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => {
      try {
        for (const message of this.#decoder.push(chunk)) this.#handleMessage(message);
      } catch (error) {
        this.#terminateForProtocolError(error);
      }
    });
    child.stderr.on('data', (chunk: string) => {
      this.#stderrTail = `${this.#stderrTail}${chunk}`.slice(-4_096);
    });
    child.on('error', (error) => {
      this.#handleExit(new DesktopHostError(`无法启动 Desktop Host：${error.message}`, 'HOST_START_FAILED'));
    });
    child.on('exit', (code, signal) => {
      if (this.#stopping) return;
      const detail = this.#stderrTail.trim();
      const suffix = detail ? `\n${detail}` : '';
      this.#handleExit(new DesktopHostError(
        `Desktop Host 意外退出（code=${code ?? 'null'}, signal=${signal ?? 'null'}）。${suffix}`,
        'HOST_EXITED',
      ));
    });
  }

  #handleMessage(message: HostMessage): void {
    if ('event' in message) {
      this.emit('event', message.event, message.payload);
      return;
    }
    const pending = this.#pending.get(message.id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.#pending.delete(message.id);
    if ('error' in message) {
      pending.reject(new DesktopHostError(message.error.message, message.error.code, message.error.details));
    } else {
      pending.resolve(message.result);
    }
  }

  #sendCancellation(requestId: string): void {
    const process = this.#process;
    if (!process || process.killed) return;
    const request = JSON.stringify({
      id: randomUUID(),
      method: 'cancel',
      params: { requestId },
    });
    process.stdin.write(`${request}\n`, 'utf8', (error) => {
      if (!error) return;
      const transportError = new DesktopHostError(
        `无法向 Desktop Host 发送取消请求：${error.message}`,
        'HOST_WRITE_FAILED',
      );
      process.kill();
      this.#handleExit(transportError);
    });
  }

  #terminateForProtocolError(error: unknown): void {
    const normalized = error instanceof Error ? error : new Error(String(error));
    this.#process?.kill();
    this.#handleExit(new DesktopHostError(normalized.message, 'HOST_PROTOCOL_ERROR'));
  }

  #handleExit(error: DesktopHostError): void {
    this.#process = null;
    this.#rejectPending(error);
    this.emit('unavailable', error.message);
  }

  #rejectPending(error: Error): void {
    for (const pending of this.#pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.#pending.clear();
  }
}

function resolveHostLaunch(): { command: string; args: string[]; hostPath: string } {
  // A packaged renderer and its ASAR are a single trust boundary. Never let
  // inherited environment variables replace the self-contained Host with an
  // arbitrary executable. Overrides remain available only for local development.
  const configuredPath = app.isPackaged ? undefined : process.env.CACHE_MANAGER_HOST_PATH?.trim();
  const candidates = app.isPackaged
    ? [path.join(
        process.resourcesPath,
        'host',
        process.platform === 'win32'
          ? 'BiliBiliLocalCacheManager.Desktop.Host.exe'
          : 'BiliBiliLocalCacheManager.Desktop.Host',
      )]
    : configuredPath
      ? [path.resolve(configuredPath)]
      : [
          path.resolve(app.getAppPath(), '..', 'BiliBiliLocalCacheManager.Desktop.Host', 'bin', 'Debug', 'net10.0', 'BiliBiliLocalCacheManager.Desktop.Host.dll'),
          path.resolve(app.getAppPath(), '..', 'BiliBiliLocalCacheManager.Desktop.Host', 'bin', 'Release', 'net10.0', 'publish', 'BiliBiliLocalCacheManager.Desktop.Host.dll'),
        ];
  const hostPath = candidates.find(existsSync);
  if (!hostPath) {
    throw new DesktopHostError(
      app.isPackaged
        ? `打包的 .NET Desktop Host 缺失：${candidates.join(', ')}。请重新安装应用。`
        : `找不到 .NET Desktop Host。已检查：${candidates.join(', ')}。可通过 CACHE_MANAGER_HOST_PATH 指定。`,
      'HOST_NOT_FOUND',
    );
  }
  if (hostPath.toLowerCase().endsWith('.dll')) {
    return {
      command: app.isPackaged ? 'dotnet' : process.env.CACHE_MANAGER_DOTNET_PATH?.trim() || 'dotnet',
      args: [hostPath, '--json-lines'],
      hostPath,
    };
  }
  return { command: hostPath, args: ['--json-lines'], hostPath };
}
