import { expect, it } from 'vitest';
import type { MediaFailure } from './contracts';
import { projectOperationResult, type OperationResult, type OperationResultFacts } from './operation-result';

const failure: MediaFailure = { avid: '123', pageIndex: 2, title: 'Part two', message: 'Missing media' };
const requirement = { avid: '123', pageIndex: 2, title: 'Part two', approvalToken: 'a'.repeat(64),
  processingKind: 'audio-aac' as const, reason: 'Audio needs conversion', impact: 'Audio will be reencoded' };

const cases: { name: string; result: OperationResult; expected: OperationResultFacts }[] = [
  { name: 'successful player handoff', result: { queued: 2, failures: [] },
    expected: { status: 'succeeded', succeeded: 2, failed: 0, unprocessed: 0, failures: [] } },
  { name: 'partial player handoff', result: { queued: 1, failures: [failure] },
    expected: { status: 'partial', succeeded: 1, failed: 1, unprocessed: 0, failures: [failure] } },
  { name: 'no player handoff', result: { queued: 0, failures: [] },
    expected: { status: 'failed', succeeded: 0, failed: 0, unprocessed: 0, failures: [] } },
  { name: 'published export', result: { published: true, exportedCount: 2, outputPath: 'D:\\exports\\batch', failures: [] },
    expected: { status: 'succeeded', succeeded: 2, failed: 0, unprocessed: 0, failures: [], outputPath: 'D:\\exports\\batch' } },
  { name: 'unpublished failed export', result: { published: false, exportedCount: 0, outputPath: null, failures: [failure] },
    expected: { status: 'failed', succeeded: 0, failed: 1, unprocessed: 0, failures: [failure] } },
  { name: 'unpublished export requiring consent', result: { published: false, exportedCount: 0, outputPath: null,
    failures: [], transcodeRequirements: [requirement], confirmationId: 'window-bound-permission' },
    expected: { status: 'confirmation', succeeded: 0, failed: 0, unprocessed: 0, failures: [] } },
  { name: 'unpublished cancelled export', result: { published: false, exportedCount: 0, outputPath: null, failures: [] },
    expected: { status: 'cancelled', succeeded: 0, failed: 0, unprocessed: 0, failures: [] } },
  { name: 'cancelled restore with committed work', result: { restored: ['one'], failed: ['two'],
    cancelled: true, unprocessed: ['three'], items: [{ entryId: 'two', avid: '123', pageIndex: 2, succeeded: false, error: 'Conflict' }] },
    expected: { status: 'cancelled', succeeded: 1, failed: 1, unprocessed: 1,
      failures: [{ avid: '123', pageIndex: 2, title: 'av123', message: 'Conflict' }] } },
  { name: 'partial permanent cleanup', result: { purged: ['one'], failed: ['two'], unprocessed: [] },
    expected: { status: 'partial', succeeded: 1, failed: 1, unprocessed: 0,
      failures: [{ avid: '', pageIndex: null, title: 'two', message: '该项未成功，请在回收站或当前操作结果中核对。' }] } },
];

it.each(cases)('projects facts for $name without carrying authorization', ({ result, expected }) => {
  const original = structuredClone(result);
  expect(projectOperationResult(result)).toEqual(expected);
  expect(result).toEqual(original);
  expect(JSON.stringify(projectOperationResult(result))).not.toMatch(/approvalToken|confirmationId|window-bound-permission/);
});

it('keeps every live failure and its full message in the failed-ID order', () => {
  const longMessage = 'Access denied: ' + 'x'.repeat(1200);
  const items = Array.from({ length: 22 }, (_, index) => ({ avid: String(1000 + index), pageIndex: 2,
    succeeded: false, error: longMessage }));
  const result = projectOperationResult({ moved: [], failed: items.map(item => `${item.avid}:P2`), items: [...items].reverse() });
  expect(result.failed).toBe(22);
  expect(result.failures).toHaveLength(22);
  expect(result.failures[0]).toEqual({ avid: '1000', pageIndex: 2, title: 'av1000', message: longMessage });
  expect(result.failures[21]).toEqual({ avid: '1021', pageIndex: 2, title: 'av1021', message: longMessage });
});

it('preserves legacy and missing detail rows alongside structured restore errors', () => {
  expect(projectOperationResult({ restored: [], failed: ['missing-details', 'conflict'], items: [
    { entryId: 'conflict', avid: '123', succeeded: false, error: 'Destination exists' },
    { entryId: 'unrelated', avid: '999', succeeded: false, error: 'Not in the failure list' },
  ] }).failures).toEqual([
    { avid: '', pageIndex: null, title: 'missing-details', message: '该项未成功，请在回收站或当前操作结果中核对。' },
    { avid: '123', pageIndex: null, title: 'av123', message: 'Destination exists' },
  ]);
});
