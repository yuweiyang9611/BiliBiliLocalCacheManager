import type {
  ExportBatchResult,
  MediaFailure,
  PlaybackBatchResult,
  TaskOutcome,
  TrashMoveResult,
  TrashPurgeResult,
  TrashRestoreResult,
} from './contracts';

export type OperationResult = PlaybackBatchResult | ExportBatchResult | TrashMoveResult | TrashRestoreResult | TrashPurgeResult;

export interface OperationResultFacts {
  status: Exclude<TaskOutcome, 'running' | 'unknown'>;
  succeeded: number;
  failed: number;
  unprocessed: number;
  failures: MediaFailure[];
  outputPath?: string;
}

// Results must already have passed the Host contract validation. These facts
// contain neither retry/transcode authorization nor history retention limits.
export function projectOperationResult(result: OperationResult): OperationResultFacts {
  if ('queued' in result) {
    return {
      status: result.queued === 0 ? 'failed' : result.failures.length ? 'partial' : 'succeeded',
      succeeded: result.queued, failed: result.failures.length, unprocessed: 0,
      failures: result.failures.map(item => ({ ...item })),
    };
  }
  if ('published' in result) {
    const confirmation = Boolean(result.transcodeRequirements?.length);
    return {
      status: result.published ? 'succeeded' : confirmation ? 'confirmation' : result.failures.length ? 'failed' : 'cancelled',
      succeeded: result.published ? result.exportedCount : 0, failed: result.failures.length, unprocessed: 0,
      failures: result.failures.map(item => ({ ...item })),
      ...(result.published && result.outputPath ? { outputPath: result.outputPath } : {}),
    };
  }

  const succeeded = 'moved' in result ? result.moved.length : 'restored' in result ? result.restored.length : result.purged.length;
  const failed = result.failed.length;
  const unprocessed = result.unprocessed?.length ?? 0;
  const details = new Map<string, { avid?: string; pageIndex?: number; error?: string }>();
  if ('moved' in result) {
    for (const item of result.items ?? []) {
      if (!item.succeeded) details.set(item.pageIndex === undefined ? item.avid : `${item.avid}:P${item.pageIndex}`, item);
    }
  } else if ('restored' in result) {
    for (const item of result.items ?? []) {
      if (!item.succeeded) details.set(item.entryId, item);
    }
  }
  return {
    status: result.cancelled ? 'cancelled' : failed || unprocessed ? succeeded ? 'partial' : 'failed' : 'succeeded',
    succeeded, failed, unprocessed,
    failures: result.failed.map(id => {
      const item = details.get(id);
      return { avid: item?.avid ?? '', pageIndex: item?.pageIndex ?? null,
        title: item?.avid ? `av${item.avid}` : id,
        message: item?.error || '该项未成功，请在回收站或当前操作结果中核对。' };
    }),
  };
}
