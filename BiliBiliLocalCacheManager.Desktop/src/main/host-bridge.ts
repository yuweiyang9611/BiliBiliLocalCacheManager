import { app } from 'electron';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { existsSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import type { JsonObject, JsonValue, OperationState } from '../shared/contracts';
import { JsonLineDecoder, type HostMessage } from './protocol';

import { HostProgressTracker } from './host-progress';

interface HostContext {
  child: ChildProcessWithoutNullStreams;
  decoder: JsonLineDecoder;
  stderrTail: string;
  ended: boolean;
  exited: Promise<void>;
  detach(): void;
}
interface PendingRequest {
  context: HostContext;
  resolve(value: JsonValue): void;
  reject(error: Error): void;
  timer: NodeJS.Timeout;
  method: string;
  cancelRequested: boolean;
  progress: HostProgressTracker;
  renew?: () => void;
}

export function hasSideEffects(method: string): boolean {
  return ['play', 'export', 'diagnostics.export', 'trash.move', 'trash.restore', 'trash.purge',
    'artifacts.cleanup', 'artifacts.clear'].includes(method);
}

export function hostTimeoutPolicy(method: string): { milliseconds: number; idle: boolean } {
  if (method === 'health') return { milliseconds: 15_000, idle: false };
  if (['initialState', 'settings.get', 'settings.update', 'search', 'cache.details', 'scan.issueLocation'].includes(method)) {
    return { milliseconds: 60_000, idle: false };
  }
  return { milliseconds: 10 * 60_000, idle: true };
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
  #context: HostContext | null = null;
  #contexts = new Set<HostContext>();
  #pending = new Map<string, PendingRequest>();
  #starting: Promise<HostContext> | null = null;
  #stopping = false;
  #states = new Map<string, OperationState>();
  readonly #trustedEnvOverrides: NodeJS.ProcessEnv;

  constructor(options: DesktopHostBridgeOptions = {}) {
    super();
    this.#trustedEnvOverrides = { ...(options.trustedEnvOverrides ?? {}) };
  }

  acknowledgeUncertain(id: string): boolean {
    const state = this.#states.get(id);
    if (state?.state !== 'unknown') return false;
    this.#state(id, state.operation, 'settled');
    return true;
  }

  #assertCanCall(method: string): void {
    if (this.#stopping) throw new DesktopHostError('桌面应用正在关闭。', 'APP_CLOSING');
    if (hasSideEffects(method) && [...this.#states.values()].some(state => state.sideEffects))
      throw new DesktopHostError('上次操作结果尚未确认，请先核对结果。', 'OUTCOME_UNCONFIRMED');
  }

  call<T>(method: string, params: JsonObject = {}, timeoutMs?: number): HostCall<T> {
    const id = randomUUID();
    let settled = false;
    let cancelRequested = false;
    const promise = (async () => {
      this.#assertCanCall(method);
      let request: string;
      try { request = JSON.stringify({ id, method, params }); }
      catch { throw new DesktopHostError('无法序列化 Desktop Host 请求。', 'INVALID_REQUEST'); }
      if (Buffer.byteLength(request, 'utf8') > MAX_HOST_REQUEST_BYTES)
        throw new DesktopHostError('Desktop Host 请求超过 1 MiB 安全上限。请减少一次操作中的项目或分段数量。', 'REQUEST_TOO_LARGE');
      const context = await this.#start();
      if (cancelRequested) throw new DesktopHostError('操作已取消。', 'CANCELLED');
      this.#assertCanCall(method);
      if (context.ended) throw new DesktopHostError('Desktop Host 已退出。', 'HOST_EXITED');
      const policy = hostTimeoutPolicy(method);
      const duration = timeoutMs ?? policy.milliseconds;
      return new Promise<T>((resolve, reject) => {
        const expire = () => {
          if (!this.#pending.has(id)) return;
          if (hasSideEffects(method)) this.#requestCancellation(id);
          else {
            this.#sendCancellation(context, id);
            this.#finish(id, new DesktopHostError('Desktop Host 调用超时：' + method, 'HOST_TIMEOUT'));
          }
        };
        const pending: PendingRequest = {
          context, method, cancelRequested: false, progress: new HostProgressTracker(),
          resolve: value => resolve(value as T), reject,
          timer: setTimeout(expire, duration),
          renew: policy.idle && timeoutMs === undefined ? () => {
            if (pending.cancelRequested) return;
            clearTimeout(pending.timer);
            pending.timer = setTimeout(expire, duration);
          } : undefined,
        };
        this.#pending.set(id, pending);
        context.child.stdin.write(request + '\n', 'utf8', error => {
          if (!error || context.ended) return;
          this.#endContext(context, new DesktopHostError('无法向 Desktop Host 写入请求：' + error.message, 'HOST_WRITE_FAILED'), true);
        });
      });
    })();
    promise.then(() => { settled = true; }, () => { settled = true; });
    return {
      id, promise,
      cancel: () => {
        if (settled || cancelRequested) return false;
        cancelRequested = true;
        if (this.#pending.has(id)) return this.#requestCancellation(id);
        return true;
      },
    };
  }

  #state(id: string, operation: string, state: OperationState['state']): void {
    const value: OperationState = { requestId: id, operation, state, sideEffects: hasSideEffects(operation) };
    if (state === 'settled') this.#states.delete(id);
    else this.#states.set(id, value);
    this.emit('operation-state', value);
  }

  #requestCancellation(id: string): boolean {
    const pending = this.#pending.get(id);
    if (!pending || pending.cancelRequested) return false;
    pending.cancelRequested = true;
    clearTimeout(pending.timer);
    this.#state(id, pending.method, 'cancelling');
    pending.timer = setTimeout(() => {
      if (this.#pending.has(id)) this.#state(id, pending.method, 'unconfirmed');
    }, 30_000);
    this.#sendCancellation(pending.context, id);
    return true;
  }

  async dispose(): Promise<void> {
    this.#stopping = true;
    await this.#starting?.catch(() => undefined);
    await Promise.all([...this.#contexts].map(async context => {
      this.#endContext(context, new DesktopHostError('桌面应用正在关闭。', 'APP_CLOSING'));
      const force = setTimeout(() => context.child.kill(), 1_500);
      context.child.stdin.end();
      try { await context.exited; }
      finally { clearTimeout(force); context.detach(); }
    }));
    this.#states.clear();
  }

  #start(): Promise<HostContext> {
    if (this.#stopping) return Promise.reject(new DesktopHostError('桌面应用正在关闭。', 'APP_CLOSING'));
    if (this.#context && !this.#context.ended) return Promise.resolve(this.#context);
    if (this.#starting) return this.#starting;
    this.#starting = Promise.resolve().then(() => {
      if (this.#stopping) throw new DesktopHostError('桌面应用正在关闭。', 'APP_CLOSING');
      return this.#spawnHost();
    }).catch((error: unknown) => {
      const normalized = error instanceof DesktopHostError ? error
        : new DesktopHostError(error instanceof Error ? error.message : String(error), 'HOST_START_FAILED');
      if (!this.#stopping) this.emit('unavailable', normalized.message);
      throw normalized;
    }).finally(() => { this.#starting = null; });
    return this.#starting;
  }

  #spawnHost(): HostContext {
    if (process.arch !== 'x64' || (process.platform !== 'win32' && process.platform !== 'linux'))
      throw new DesktopHostError('当前版本不支持 ' + process.platform + '/' + process.arch, 'UNSUPPORTED_PLATFORM');
    const launch = resolveHostLaunch();
    const child = spawn(launch.command, launch.args, {
      cwd: path.dirname(launch.hostPath),
      env: {
        ...createHostEnvironment(process.env, app.isPackaged, this.#trustedEnvOverrides),
        DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      },
      shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
    });
    let exited!: () => void;
    const context: HostContext = {
      child, decoder: new JsonLineDecoder(), stderrTail: '', ended: false,
      exited: new Promise<void>(resolve => { exited = resolve; }), detach: () => {},
    };
    this.#context = context;
    this.#contexts.add(context);
    const data = (chunk: string) => {
      if (context.ended || this.#context !== context) return;
      try {
        for (const message of context.decoder.push(chunk)) this.#handleMessage(context, message);
      } catch (error) {
        this.#endContext(context, new DesktopHostError(error instanceof Error ? error.message : String(error), 'HOST_PROTOCOL_ERROR'), true);
      }
    };
    const stderr = (chunk: string) => {
      if (!context.ended) context.stderrTail = (context.stderrTail + chunk).slice(-4_096);
    };
    const error = (value: Error) => {
      this.#endContext(context, new DesktopHostError('Desktop Host 进程错误：' + value.message, 'HOST_START_FAILED'), true);
    };
    const exit = (code: number | null, signal: string | null) => {
      exited();
      this.#endContext(context, new DesktopHostError(
        'Desktop Host 意外退出（code=' + code + ', signal=' + signal + '）。' + context.stderrTail.trim(), 'HOST_EXITED'));
      context.detach();
    };
    const close = () => exit(null, null);
    context.detach = () => {
      child.stdout.removeListener('data', data);
      child.stderr.removeListener('data', stderr);
      child.removeListener('exit', exit);
      child.removeListener('close', close);
      child.removeListener('error', error);
      this.#contexts.delete(context);
    };
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', data);
    child.stderr.on('data', stderr);
    child.on('error', error);
    child.once('exit', exit);
    child.once('close', close);
    return context;
  }

  #handleMessage(context: HostContext, message: HostMessage): void {
    if (context.ended) return;
    if ('event' in message) {
      if (message.event === 'progress' && message.payload && typeof message.payload === 'object' && !Array.isArray(message.payload)) {
        const id = message.payload.requestId;
        const pending = typeof id === 'string' ? this.#pending.get(id) : undefined;
        if (pending?.context === context && pending.progress.advance(message.payload, pending.method)) pending.renew?.();
      }
      this.emit('event', message.event, message.payload);
      return;
    }
    if (this.#pending.get(message.id)?.context !== context) return;
    if ('error' in message) this.#finish(message.id, new DesktopHostError(message.error.message, message.error.code, message.error.details));
    else this.#finish(message.id, undefined, message.result);
  }

  #finish(id: string, error?: Error, value?: JsonValue): void {
    const pending = this.#pending.get(id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.#pending.delete(id);
    if (this.#states.has(id)) this.#state(id, pending.method, 'settled');
    if (error) pending.reject(error);
    else pending.resolve(value ?? null);
  }

  #sendCancellation(context: HostContext, requestId: string): void {
    if (context.ended) return;
    const request = JSON.stringify({ id: randomUUID(), method: 'cancel', params: { requestId } });
    context.child.stdin.write(request + '\n', 'utf8', error => {
      if (error && !context.ended)
        this.#endContext(context, new DesktopHostError('无法发送取消请求：' + error.message, 'HOST_WRITE_FAILED'), true);
    });
  }

  #endContext(context: HostContext, error: DesktopHostError, kill = false): void {
    if (context.ended) return;
    context.ended = true;
    if (this.#context === context) this.#context = null;
    for (const [id, pending] of this.#pending) {
      if (pending.context !== context) continue;
      clearTimeout(pending.timer);
      this.#pending.delete(id);
      if (hasSideEffects(pending.method) && !this.#stopping) {
        this.#state(id, pending.method, 'unknown');
        pending.reject(new DesktopHostError('操作结果无法确认，请核对输出或操作结果后再继续。', 'OUTCOME_UNKNOWN'));
      } else {
        if (this.#states.has(id)) this.#state(id, pending.method, 'settled');
        pending.reject(error);
      }
    }
    if (kill) context.child.kill();
    if (!this.#stopping) this.emit('unavailable', error.message);
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
