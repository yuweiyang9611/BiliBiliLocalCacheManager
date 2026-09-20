import type { CachePage } from '../shared/contracts';

export type Page = 'library' | 'storage' | 'trash' | 'settings' | 'diagnostics';

export type Notice = { id: number; kind: 'success' | 'error' | 'info'; message: string };

export type Activity = { time: Date; kind: 'success' | 'error' | 'info'; message: string };

export type CachePageState = Pick<CachePage, 'offset' | 'pageSize' | 'totalItems' | 'hasMore'>;
