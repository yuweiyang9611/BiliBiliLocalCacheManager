import { expect, it } from 'vitest';
import { cancellationWarning, exportRollbackWarning, isCancellationCode } from './cancellation';

const unverified = '导出已取消。无法确认暂存目录的身份，已跳过清理。请核对：D:\\exports\\.staging-1';
const uncleaned = '导出已取消。导出暂存目录未能完全清理，请核对：/home/user/exports/.staging-1';

it.each(['cancelled', 'CANCELLED'])('recognizes the %s cancellation code', code => {
  expect(isCancellationCode(code)).toBe(true);
});

it.each([undefined, 'canceled', 'stale_index', 'OUTCOME_UNKNOWN'])('does not treat %s as cancellation', code => {
  expect(isCancellationCode(code)).toBe(false);
});

it.each([undefined, 42, '', '   ', 'Cancelled', 'The request was cancelled.', '操作已取消。', '导出已取消。',
  "Error invoking remote method 'cache:export': The request was cancelled.",
  "Error invoking remote method 'cache:export': Error: 导出已取消。",
  '导出已取消。请核对：D:\\exports', '导出已取消。导出暂存目录未能完全清理，请核对：', '诊断信息：D:\\private\\log.txt',
  `前缀 ${unverified}`])('does not treat %j as an export rollback warning', message => {
  expect(exportRollbackWarning(message)).toBeUndefined();
});

it.each([unverified, uncleaned])('keeps the Host rollback warning %j', message => {
  expect(exportRollbackWarning(`  ${message}  `)).toBe(message);
});

it('requires a cancellation code before treating an error message as a rollback warning', () => {
  expect(cancellationWarning(Object.assign(new Error(unverified), { code: 'cancelled' }))).toBe(unverified);
  expect(cancellationWarning(Object.assign(new Error(unverified), { code: 'EXPORT_FAILED' }))).toBeUndefined();
  expect(cancellationWarning(new Error(unverified))).toBeUndefined();
  expect(cancellationWarning(Object.assign(new Error('The request was cancelled.'), { code: 'cancelled' }))).toBeUndefined();
});
