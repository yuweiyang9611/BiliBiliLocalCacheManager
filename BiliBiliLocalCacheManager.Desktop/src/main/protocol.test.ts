import { describe, expect, it } from 'vitest';
import path from 'node:path';
import { JsonLineDecoder, parseHostMessage } from './protocol';
import { resolveRendererFilePath } from './renderer-protocol';

describe('JSON-lines protocol', () => {
  it('decodes split responses and progress events', () => {
    const decoder = new JsonLineDecoder();
    expect(decoder.push('{"id":"1","res')).toEqual([]);
    expect(decoder.push('ult":{"ok":true}}\n{"event":"progress","payload":{"percentage":50}}\n')).toEqual([
      { id: '1', result: { ok: true } },
      { event: 'progress', payload: { percentage: 50 } },
    ]);
  });

  it('rejects malformed error envelopes', () => {
    expect(() => parseHostMessage('{"id":"1","error":"bad"}')).toThrow(/错误对象/);
  });
});

describe('packaged renderer protocol', () => {
  it('maps only the declared app host into the renderer directory', () => {
    const root = path.resolve('application', 'dist');
    expect(resolveRendererFilePath(root, 'blcm://app/index.html')).toBe(
      path.join(root, 'index.html'),
    );
    expect(resolveRendererFilePath(root, 'blcm://other/index.html')).toBeNull();
    expect(resolveRendererFilePath(root, 'https://app/index.html')).toBeNull();
  });

  it('rejects encoded traversal and credentials', () => {
    const root = path.resolve('application', 'dist');
    expect(resolveRendererFilePath(root, 'blcm://app/%2e%2e%5csecret.txt')).toBeNull();
    expect(resolveRendererFilePath(root, 'blcm://user@app/index.html')).toBeNull();
  });
});
