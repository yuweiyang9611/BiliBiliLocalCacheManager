import type { CacheManagerApi } from '../shared/contracts';
import { unwrapIpcResult } from '../shared/ipc-result';

// Decode after contextBridge, where custom Error properties would otherwise be lost.
export const cacheManager: CacheManagerApi = new Proxy({} as CacheManagerApi, {
  get: (_target, key: keyof CacheManagerApi) => (...args: unknown[]) => {
    const method = window.cacheManager[key] as (...values: unknown[]) => unknown;
    const result = method(...args);
    return result instanceof Promise ? result.then(unwrapIpcResult) : result;
  },
});
