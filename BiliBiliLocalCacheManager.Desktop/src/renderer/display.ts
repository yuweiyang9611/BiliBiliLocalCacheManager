import type { CacheEntry, TrashEntry, ArtifactCleanupResult } from '../shared/contracts';
import type { Notice } from './ui-types';

export function toggleSet(current: Set<string>, value: string): Set<string> { const next = new Set(current); if (next.has(value)) next.delete(value); else next.add(value); return next; }

export function selectedBytes(items: CacheEntry[], selected: Set<string>): number { return items.reduce((sum, item) => sum + (selected.has(item.id) ? item.sizeBytes : 0), 0); }

export function safeName(value: string): string { return value.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').slice(0, 80) || '缓存导出'; }

export function dateStamp(): string { const value = new Date(); return `${value.getFullYear()}${String(value.getMonth() + 1).padStart(2, '0')}${String(value.getDate()).padStart(2, '0')}-${String(value.getHours()).padStart(2, '0')}${String(value.getMinutes()).padStart(2, '0')}`; }

export function formatBytes(bytes: number): string { if (!Number.isFinite(bytes) || bytes <= 0) return '0 MB'; if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`; return `${(bytes / 1024 ** 2).toFixed(1)} MB`; }

export function formatDuration(seconds: number): string { if (!Number.isFinite(seconds) || seconds <= 0) return '—'; const total = Math.round(seconds); const h = Math.floor(total / 3600); const m = Math.floor((total % 3600) / 60); const s = total % 60; return h ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`; }

export function formatDate(value: string | null): string { if (!value) return '未知'; const date = new Date(value); return Number.isNaN(date.getTime()) ? value : date.toLocaleString([], { dateStyle: 'short', timeStyle: 'short' }); }

export function trashTime(entry: TrashEntry): number { if (!entry.deletedAt) return 0; const value = new Date(entry.deletedAt).getTime(); return Number.isNaN(value) ? 0 : value; }

export type DiskOutcome = { failed: string[]; cancelled?: boolean; unprocessed?: string[] };

export function diskOutcomeKind(result: DiskOutcome): Notice['kind'] { return result.failed.length ? 'error' : result.cancelled ? 'info' : 'success'; }

export function diskOutcomeMessage(action: string, count: number, result: DiskOutcome, failed = result.failed.length): string {
  return `已${action} ${count} 项，失败 ${failed} 项${result.unprocessed?.length ? `，未执行 ${result.unprocessed.length} 项` : ''}。${result.cancelled ? '已取消剩余操作。' : ''}`;
}

export function artifactCleanupMessage(prefix: string, result: ArtifactCleanupResult): string {
  return `${result.cancelled ? '清理已停止' : prefix}：删除 ${result.deletedFileCount} 个文件，释放 ${formatBytes(result.freedBytes)}，失败 ${result.failedFileCount} 个，未执行 ${result.unprocessedFileCount ?? 0} 个，剩余${result.remainingBytesEstimated ? '约' : ''} ${formatBytes(result.remainingBytes)}。`;
}
