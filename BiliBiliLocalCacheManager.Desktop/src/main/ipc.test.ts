import { EventEmitter } from 'node:events';
import path from 'node:path';
import type { IpcMainInvokeEvent } from 'electron';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { JsonObject } from '../shared/contracts';
import { channels } from '../shared/channels';
import type { DesktopHostBridge } from './host-bridge';

const electronMocks = vi.hoisted(() => ({
  handlers: new Map<string, (...args: unknown[]) => unknown>(),
  handle: vi.fn((channel: string, listener: (...args: unknown[]) => unknown) => {
    electronMocks.handlers.set(channel, listener);
  }),
  removeHandler: vi.fn((channel: string) => {
    electronMocks.handlers.delete(channel);
  }),
  fromWebContents: vi.fn(() => null),
  getPath: vi.fn(() => path.resolve('downloads')),
  showOpenDialog: vi.fn(),
  showSaveDialog: vi.fn(),
  showMessageBox: vi.fn(),
  openPath: vi.fn(),
}));

vi.mock('electron', () => ({
  BrowserWindow: { fromWebContents: electronMocks.fromWebContents },
  app: {
    getPath: electronMocks.getPath,
    getVersion: () => '0.4.0',
  },
  dialog: {
    showOpenDialog: electronMocks.showOpenDialog,
    showSaveDialog: electronMocks.showSaveDialog,
    showMessageBox: electronMocks.showMessageBox,
  },
  ipcMain: {
    handle: electronMocks.handle,
    removeHandler: electronMocks.removeHandler,
  },
  shell: { openPath: electronMocks.openPath },
}));

import { registerIpc } from './ipc';

interface Deferred {
  promise: Promise<unknown>;
  resolve(value: unknown): void;
  reject(error: unknown): void;
}

let unregister: (() => void) | undefined;

beforeEach(() => {
  electronMocks.handlers.clear();
  vi.clearAllMocks();
  electronMocks.getPath.mockReturnValue(path.resolve('downloads'));
});

afterEach(() => {
  unregister?.();
  unregister = undefined;
});

describe('IPC cancellable request tracking', () => {
  it('cancels every concurrent request from the renderer, including search', async () => {
    const fake = createDeferredBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(7);

    const scan = invoke(channels.scan, event, { rootPath: path.resolve('cache'), includeIncomplete: false });
    const search = invoke(channels.search, event, validSearchRequest());
    const requestIds = fake.calls
      .filter((call) => call.method === 'scan' || call.method === 'search')
      .map((call) => call.id);

    expect(await invoke(channels.cancel, event)).toBe(true);
    expect(fake.calls.filter((call) => call.method === 'cancel').map((call) => call.params.requestId))
      .toEqual(requestIds);

    for (const requestId of requestIds) fake.pending.get(requestId)?.resolve([]);
    await Promise.all([scan, search]);
  });

  it('does not let an earlier completion remove a later active request', async () => {
    const fake = createDeferredBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(11);

    const scan = invoke(channels.scan, event, { rootPath: path.resolve('cache'), includeIncomplete: false });
    const scanId = fake.calls.find((call) => call.method === 'scan')!.id;
    const search = invoke(channels.search, event, validSearchRequest());
    const searchId = fake.calls.find((call) => call.method === 'search')!.id;

    fake.pending.get(scanId)!.resolve({ items: [] });
    await scan;
    expect(await invoke(channels.cancel, event)).toBe(true);
    expect(fake.calls.filter((call) => call.method === 'cancel').map((call) => call.params.requestId))
      .toEqual([searchId]);

    fake.pending.get(searchId)!.resolve([]);
    await search;
    expect(await invoke(channels.cancel, event)).toBe(false);
  });
});

describe('IPC export destinations', () => {
  it('uses a save-file dialog only for one explicitly selected page', async () => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(17);
    const selectedPath = path.resolve('exports', 'one.mp4');
    electronMocks.showSaveDialog.mockResolvedValue({ canceled: false, filePath: selectedPath });

    await invoke(
      channels.exportMedia,
      event,
      path.resolve('cache'),
      [{ avid: '123', pageIndexes: [1] }],
      '../../renderer-controlled.mp4',
      false,
    );

    expect(electronMocks.showSaveDialog).toHaveBeenCalledWith(expect.objectContaining({
      title: '导出 MP4',
      defaultPath: path.join(path.resolve('downloads'), 'renderer-controlled.mp4'),
    }));
    expect(electronMocks.showOpenDialog).not.toHaveBeenCalled();
    expect(fake.calls.find((call) => call.method === 'export')?.params.outputPath).toBe(selectedPath);
  });

  it.each([
    ['multiple pages', [{ avid: '123', pageIndexes: [1, 2] }]],
    ['multiple caches', [{ avid: '123' }, { avid: '456' }]],
    ['a whole cache whose page count is resolved by the Host', [{ avid: '123' }]],
  ])('uses a directory dialog for %s', async (_label, targets) => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(23);
    const selectedDirectory = path.resolve('exports', 'batch');
    electronMocks.showOpenDialog.mockResolvedValue({
      canceled: false,
      filePaths: [selectedDirectory],
    });

    await invoke(channels.exportMedia, event, path.resolve('cache'), targets, 'batch.mp4', false);

    expect(electronMocks.showOpenDialog).toHaveBeenCalledWith(expect.objectContaining({
      title: '选择 MP4 导出目录',
      properties: ['openDirectory', 'createDirectory'],
    }));
    expect(electronMocks.showSaveDialog).not.toHaveBeenCalled();
    expect(fake.calls.find((call) => call.method === 'export')?.params.outputPath)
      .toBe(selectedDirectory);
  });
});

describe('IPC trash root safety', () => {
  it('requires an explicit root and a non-empty entry snapshot before showing confirmation', async () => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(29);

    await expect(invoke(channels.trashPurge, event, '', ['entry-1'])).rejects.toThrow('rootPath');
    await expect(invoke(channels.trashPurge, event, path.resolve('cache'), [])).rejects.toThrow('至少包含 1 项');

    expect(electronMocks.showMessageBox).not.toHaveBeenCalled();
    expect(fake.calls.some((call) => call.method === 'trash.purge')).toBe(false);
  });

  it('binds permanent purge to the displayed root and complete entry id snapshot', async () => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(31);
    const rootPath = path.resolve('cache-b');
    electronMocks.showMessageBox.mockResolvedValue({ response: 0 });

    await invoke(channels.trashPurge, event, rootPath, ['entry-1', 'entry-2']);

    expect(electronMocks.showMessageBox).toHaveBeenCalledWith(expect.objectContaining({
      detail: expect.stringContaining(rootPath),
    }));
    expect(fake.calls.find((call) => call.method === 'trash.purge')?.params).toEqual({
      rootPath,
      entryIds: ['entry-1', 'entry-2'],
      confirmed: true,
    });
  });

  it('accepts a complete purge snapshot larger than the generic batch limit', async () => {
    const fake = createImmediateBridge();
    unregister = registerIpc(fake.bridge, () => null);
    const event = trustedEvent(33);
    const rootPath = path.resolve('large-trash');
    const entryIds = Array.from({ length: 1_001 }, (_, index) => `entry-${index}`);
    electronMocks.showMessageBox.mockResolvedValue({ response: 0 });

    await invoke(channels.trashPurge, event, rootPath, entryIds);

    expect(fake.calls.find((call) => call.method === 'trash.purge')?.params).toEqual({
      rootPath,
      entryIds,
      confirmed: true,
    });
  });
});

function createDeferredBridge(): {
  bridge: DesktopHostBridge;
  calls: Array<{ id: string; method: string; params: JsonObject }>;
  pending: Map<string, Deferred>;
} {
  const calls: Array<{ id: string; method: string; params: JsonObject }> = [];
  const pending = new Map<string, Deferred>();
  let sequence = 0;
  const emitter = new EventEmitter() as EventEmitter & {
    call<T>(method: string, params?: JsonObject): { id: string; promise: Promise<T> };
  };
  emitter.call = <T>(method: string, params: JsonObject = {}) => {
    const id = `${method}-${++sequence}`;
    calls.push({ id, method, params });
    if (method === 'cancel') {
      return {
        id,
        promise: Promise.resolve({
          requestId: params.requestId,
          cancelled: true,
        } as T),
      };
    }
    const operation = deferred();
    pending.set(id, operation);
    return { id, promise: operation.promise as Promise<T> };
  };
  return { bridge: emitter as unknown as DesktopHostBridge, calls, pending };
}

function createImmediateBridge(): {
  bridge: DesktopHostBridge;
  calls: Array<{ id: string; method: string; params: JsonObject }>;
} {
  const calls: Array<{ id: string; method: string; params: JsonObject }> = [];
  let sequence = 0;
  const emitter = new EventEmitter() as EventEmitter & {
    call<T>(method: string, params?: JsonObject): { id: string; promise: Promise<T> };
  };
  emitter.call = <T>(method: string, params: JsonObject = {}) => {
    const id = `${method}-${++sequence}`;
    calls.push({ id, method, params });
    return {
      id,
      promise: Promise.resolve({ outputPath: params.outputPath } as T),
    };
  };
  return { bridge: emitter as unknown as DesktopHostBridge, calls };
}

function deferred(): Deferred {
  let resolve!: (value: unknown) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<unknown>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function trustedEvent(senderId: number): IpcMainInvokeEvent {
  const frame = { url: 'blcm://app/index.html', top: null as unknown };
  frame.top = frame;
  return {
    sender: { id: senderId },
    senderFrame: frame,
  } as unknown as IpcMainInvokeEvent;
}

function invoke(channel: string, event: IpcMainInvokeEvent, ...args: unknown[]): Promise<unknown> {
  const handler = electronMocks.handlers.get(channel);
  if (!handler) throw new Error(`Missing IPC handler for ${channel}`);
  return Promise.resolve(handler(event, ...args));
}

function validSearchRequest(): JsonObject {
  return {
    rootPath: path.resolve('cache'),
    includeIncomplete: false,
    keyword: 'demo',
    matchMode: 'contains',
    splitKeywords: true,
    anyKeywords: false,
    includePartName: true,
    includeOwnerName: true,
    includeBvid: true,
    includeAvid: true,
    caseSensitive: false,
  };
}
