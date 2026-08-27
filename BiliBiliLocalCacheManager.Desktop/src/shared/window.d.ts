import type { CacheManagerApi } from './contracts';

declare global {
  interface Window {
    cacheManager: CacheManagerApi;
  }
}

export {};
