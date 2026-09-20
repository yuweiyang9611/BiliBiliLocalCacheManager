import { describe, expect, it } from 'vitest';
import { errorCode, ipcFailure, IpcError, unwrapIpcResult } from './ipc-result';

describe('IPC error envelopes', () => {
  it.each(['cancelled', 'stale_index', 'OUTCOME_UNKNOWN', 'OUTCOME_UNCONFIRMED'])('preserves %s through serialization independently of message wording', code => {
    const wire = structuredClone(ipcFailure(new IpcError('Message without classification keywords', code)));
    try { unwrapIpcResult(wire); throw new Error('Expected rejection'); }
    catch (error) { expect(errorCode(error)).toBe(code); }
  });
  it('does not classify ordinary error prose as cancellation', () => {
    expect(errorCode(new Error('取消失败，结果无法确认'))).toBeUndefined();
    expect(ipcFailure(new TypeError('Invalid params')).ipcFailure.code).toBe('INVALID_REQUEST');
  });
  it('preserves successful values and rejects malformed failure envelopes', () => {
    const value = { queued: 1, failures: [] };
    expect(unwrapIpcResult(value)).toBe(value);
    expect(() => unwrapIpcResult({ ipcFailure: null } as never)).toThrow('Invalid IPC');
  });
});
