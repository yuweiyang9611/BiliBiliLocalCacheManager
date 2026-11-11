import { mkdir, open, readFile, rename, stat, unlink } from 'node:fs/promises';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { randomUUID } from 'node:crypto';
import type { JsonObject, TaskHistoryEntry, TaskHistorySnapshot, TaskOperation, TaskOutcome } from '../shared/contracts';
import { validateExportBatchResult, validatePlaybackBatchResult, validateTrashMoveResult,
  validateTrashRestoreResult, validateTrashPurgeResult, validateTrashSnapshotPurgeResult } from './host-contract-validation';
import { projectOperationResult } from '../shared/operation-result';

const operations = new Set<TaskOperation>(['play', 'export', 'trash.move', 'trash.restore', 'trash.purge', 'trash.purgeSnapshot']);
const statuses = new Set<TaskOutcome>(['running', 'succeeded', 'partial', 'failed', 'cancelled', 'confirmation', 'unknown']);
const capacity = 100;
const retention = 30 * 24 * 60 * 60 * 1000;
const maximumFileBytes = 64 * 1024 * 1024;
const bounded = (value: unknown, maximum = 32768): string | undefined => typeof value === 'string'
  ? value.length > maximum ? value.slice(0, maximum - 1) + '…' : value : undefined;
const count = (value: unknown): value is number => Number.isSafeInteger(value) && (value as number) >= 0;

export function isRecordedTask(method: string): method is TaskOperation { return operations.has(method as TaskOperation); }

// Read-only history of observations, never authorization to replay a task. Writes
// are serialized and atomically replace only this application's history file.
export class TaskHistoryStore extends EventEmitter {
  #entries: TaskHistoryEntry[] = [];
  #warning: string | undefined;
  #readOnly = false;
  #queue: Promise<void>;
  constructor(private readonly file: string, private readonly now: () => number = Date.now) {
    super();
    this.#queue = this.#load();
  }

  async #load(): Promise<void> {
    try {
      if ((await stat(this.file)).size > maximumFileBytes) throw new Error('History too large');
      const stored = JSON.parse(await readFile(this.file, 'utf8')) as { schemaVersion?: unknown; entries?: unknown };
      if (typeof stored.schemaVersion === 'number' && stored.schemaVersion > 1) {
        this.#readOnly = true;
        this.#warning = '任务历史来自较新版本，当前版本不会覆盖它。';
        return;
      }
      if (stored.schemaVersion !== 1 || !Array.isArray(stored.entries) || stored.entries.length > capacity) throw new Error('Invalid history');
      const loaded = stored.entries.map(validateEntry);
      this.#entries = loaded.map(entry => entry.status === 'running'
        ? { ...entry, status: 'unknown', summary: '上次运行未留下确认结果，请核对输出文件或缓存状态。' } : entry);
      const pruned = this.#prune();
      if (pruned || loaded.some(entry => entry.status === 'running')) await this.#save();
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return;
      // Preserve the original evidence on corruption or read failures.
      this.#readOnly = true;
      this.#warning = '无法读取任务历史，原文件已保留。本次任务记录仅保存在内存中。';
    }
  }

  #prune() {
    const before = this.#entries.length;
    this.#entries = this.#entries.filter(entry => entry.status === 'running' || Date.parse(entry.startedAt) >= this.now() - retention).slice(0, capacity);
    return this.#entries.length !== before;
  }

  async #save(): Promise<void> {
    if (this.#readOnly) return;
    const temporary = `${this.file}.${randomUUID()}.writing`;
    let ownsTemporary = false;
    try {
      await mkdir(path.dirname(this.file), { recursive: true });
      const handle = await open(temporary, 'wx', 0o600);
      ownsTemporary = true;
      try { await handle.writeFile(JSON.stringify({ schemaVersion: 1, entries: this.#entries })); await handle.sync(); }
      finally { await handle.close(); }
      await rename(temporary, this.file);
      this.#warning = undefined;
    } catch {
      this.#warning = '任务历史暂时无法写入磁盘；当前显示的结果可能无法在重启后保留。';
      if (ownsTemporary) await unlink(temporary).catch(() => undefined);
    }
  }

  #mutate(action: () => void): Promise<void> {
    this.#queue = this.#queue.then(async () => {
      action(); this.#prune();
      await this.#save();
      this.emit('changed');
    });
    return this.#queue;
  }

  start(id: string, operation: TaskOperation, parameters: JsonObject): Promise<void> {
    return this.#mutate(() => this.#entries.unshift({ id, operation, startedAt: new Date(this.now()).toISOString(),
      status: 'running', rootPath: bounded(parameters.rootPath), summary: '任务已提交，等待最终结果。',
      succeeded: 0, failed: 0, unprocessed: 0, failures: [] }));
  }

  finish(id: string, operation: TaskOperation, result: unknown, error?: unknown): Promise<void> {
    return this.#mutate(() => {
      const entry = this.#entries.find(value => value.id === id);
      if (!entry) return;
      entry.recordedAt = new Date(this.now()).toISOString();
      try {
        if (error !== undefined) throw error;
        Object.assign(entry, summarize(operation, result));
      } catch (failure) {
        const code = failure && typeof failure === 'object' && 'code' in failure ? failure.code : undefined;
        const cancelled = code === 'cancelled' || code === 'CANCELLED';
        entry.status = cancelled ? 'cancelled' : 'unknown';
        entry.summary = cancelled ? '任务已取消。'
          : '未取得可确认的完成结果，请核对输出文件或缓存状态；历史记录不会自动重试。';
      }
    });
  }

  async snapshot(): Promise<TaskHistorySnapshot> {
    await this.#queue;
    if (this.#prune()) await this.#mutate(() => {});
    return { entries: structuredClone(this.#entries), warning: this.#warning };
  }
  async flush(): Promise<void> { await this.#queue; }

  async publishedExportPath(id: string): Promise<string | undefined> {
    await this.#queue;
    return this.#entries.find(entry => entry.id === id && entry.operation === 'export' &&
      entry.status === 'succeeded' && entry.succeeded > 0 && Date.parse(entry.startedAt) >= this.now() - retention)?.outputPath;
  }
}

function summarize(operation: TaskOperation, result: unknown): Pick<TaskHistoryEntry, 'status' | 'summary' | 'succeeded' | 'failed' | 'unprocessed' | 'failures' | 'outputPath'> {
  const response = operation === 'play' ? validatePlaybackBatchResult(result)
    : operation === 'export' ? validateExportBatchResult(result)
      : operation === 'trash.move' ? validateTrashMoveResult(result)
        : operation === 'trash.restore' ? validateTrashRestoreResult(result)
          : operation === 'trash.purgeSnapshot' ? validateTrashSnapshotPurgeResult(result) : validateTrashPurgeResult(result);
  const facts = projectOperationResult(response);
  const { status, succeeded } = facts;
  const summary = operation === 'play' ? `已交给播放器 ${succeeded} 项；这不代表播放完成。`
    : operation === 'export' ? status === 'confirmation' ? '本批未发布，等待转码确认。请从缓存库重新选择并核对。'
      : `已发布 ${succeeded} 项。` : `已完成 ${succeeded} 项。`;
  return { ...facts, summary, outputPath: bounded(facts.outputPath),
    failures: facts.failures.slice(0, 20).map(item => ({ avid: item.avid, pageIndex: item.pageIndex,
      title: bounded(item.title, 512)!, message: bounded(item.message, 1024)! })) };
}

function validateEntry(value: unknown): TaskHistoryEntry {
  if (!value || typeof value !== 'object') throw new Error('Invalid entry');
  const item = value as TaskHistoryEntry;
  if (typeof item.id !== 'string' || item.id.length > 128 || !isRecordedTask(item.operation) || !statuses.has(item.status)
    || typeof item.startedAt !== 'string' || item.startedAt.length > 40 || !Number.isFinite(Date.parse(item.startedAt))
    || (item.recordedAt !== undefined && (typeof item.recordedAt !== 'string' || item.recordedAt.length > 40 || !Number.isFinite(Date.parse(item.recordedAt))))
    || !count(item.succeeded) || !count(item.failed) || !count(item.unprocessed)
    || !Array.isArray(item.failures) || item.failures.length > 20 || typeof item.summary !== 'string') throw new Error('Invalid entry');
  return { id: item.id, operation: item.operation, status: item.status, startedAt: item.startedAt, recordedAt: item.recordedAt,
    rootPath: bounded(item.rootPath), outputPath: bounded(item.outputPath), summary: bounded(item.summary, 1024)!,
    succeeded: item.succeeded, failed: item.failed, unprocessed: item.unprocessed,
    failures: item.failures.map(failure => {
      if (!failure || typeof failure.avid !== 'string' || (failure.pageIndex !== null && !count(failure.pageIndex))
        || typeof failure.title !== 'string' || typeof failure.message !== 'string') throw new Error('Invalid failure');
      return { avid: bounded(failure.avid, 32)!, pageIndex: failure.pageIndex, title: bounded(failure.title, 512)!, message: bounded(failure.message, 1024)! };
    }) };
}
