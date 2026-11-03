import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { afterEach, beforeEach, expect, it } from 'vitest';
import { TaskHistoryStore } from './task-history';

let directory: string;
let file: string;
beforeEach(async () => { directory = await mkdtemp(path.join(os.tmpdir(), 'blcm-history-')); file = path.join(directory, 'tasks.json'); });
afterEach(async () => { await rm(directory, { recursive: true, force: true }); });

it('persists published results and failure details across restart without authorization tokens', async () => {
  const history = new TaskHistoryStore(file);
  await history.start('export-1', 'export', { rootPath: 'D:\\cache', indexToken: 'must-not-save', transcodeApprovals: ['secret'] });
  await history.finish('export-1', 'export', { published: true, outputPath: 'D:\\exports\\batch', exportedCount: 2, failures: [] });
  await history.start('play-1', 'play', {});
  await history.finish('play-1', 'play', { queued: 1, failures: [{ avid: '123', pageIndex: 2, title: 'Part 2', message: 'Unavailable' }] });
  const reopened = await new TaskHistoryStore(file).snapshot();
  expect(reopened.warning).toBeUndefined();
  expect(reopened.entries.map(item => item.status)).toEqual(['partial', 'succeeded']);
  expect(reopened.entries[1].outputPath).toBe('D:\\exports\\batch');
  expect(reopened.entries[0].failures[0].pageIndex).toBe(2);
  const saved = await readFile(file, 'utf8');
  expect(saved).not.toMatch(/must-not-save|secret|transcodeApprovals/);
});

it.each(['trash.move', 'trash.restore'] as const)('preserves %s failure reasons and part identities across restart', async operation => {
  const history = new TaskHistoryStore(file);
  await history.start('trash-parts', operation, { rootPath: 'D:\\cache' });
  await history.finish('trash-parts', operation, operation === 'trash.move' ? {
    moved: ['456:P1'], entryIds: ['trash-success'], failed: ['123:P2', '123:P3'],
    items: [
      { avid: '123', pageIndex: 3, succeeded: false, error: 'Access to this part was denied.' },
      { avid: '456', pageIndex: 1, succeeded: true, entryId: 'trash-success' },
      { avid: '123', pageIndex: 2, succeeded: false, error: 'Original destination already exists.' },
    ],
  } : {
    restored: ['trash-success'], failed: ['trash-conflict', 'trash-denied'],
    items: [
      { entryId: 'trash-denied', avid: '123', pageIndex: 3, succeeded: false, error: 'Access to this part was denied.' },
      { entryId: 'trash-success', avid: '456', pageIndex: 1, succeeded: true },
      { entryId: 'trash-conflict', avid: '123', pageIndex: 2, succeeded: false, error: 'Original destination already exists.' },
    ],
  });

  const reopened = await new TaskHistoryStore(file).snapshot();
  expect(reopened.warning).toBeUndefined();
  expect(reopened.entries[0]).toMatchObject({ operation, status: 'partial', succeeded: 1, failed: 2 });
  expect(reopened.entries[0].failures).toEqual([
    { avid: '123', pageIndex: 2, title: 'av123', message: 'Original destination already exists.' },
    { avid: '123', pageIndex: 3, title: 'av123', message: 'Access to this part was denied.' },
  ]);
});

it.each(['trash.move', 'trash.restore'] as const)('preserves %s whole-video details and missing-detail fallbacks across restart', async operation => {
  const history = new TaskHistoryStore(file);
  await history.start('trash-video', operation, {});
  await history.finish('trash-video', operation, operation === 'trash.move' ? {
    moved: [], failed: ['456', '123'],
    items: [{ avid: '123', succeeded: false, error: 'Video directory identity changed.' }],
  } : {
    restored: [], failed: ['456', 'trash-video'],
    items: [{ entryId: 'trash-video', avid: '123', succeeded: false, error: 'Video directory identity changed.' }],
  });

  const reopened = await new TaskHistoryStore(file).snapshot();
  expect(reopened.warning).toBeUndefined();
  expect(reopened.entries[0].failures).toEqual([
    { avid: '', pageIndex: null, title: '456', message: '该项未成功，请在回收站或当前操作结果中核对。' },
    { avid: '123', pageIndex: null, title: 'av123', message: 'Video directory identity changed.' },
  ]);
});

it('preserves restore errors without video metadata and legacy results across restart', async () => {
  const history = new TaskHistoryStore(file);
  await history.start('missing', 'trash.restore', {});
  await history.finish('missing', 'trash.restore', { restored: [], failed: ['trash-missing'],
    items: [{ entryId: 'trash-missing', succeeded: false, error: 'Trash entry no longer exists.' }] });
  await history.start('legacy', 'trash.move', {});
  await history.finish('legacy', 'trash.move', { moved: [], failed: ['123'] });

  const reopened = await new TaskHistoryStore(file).snapshot();
  expect(reopened.warning).toBeUndefined();
  expect(reopened.entries.map(entry => entry.failures)).toEqual([
    [{ avid: '', pageIndex: null, title: '123', message: '该项未成功，请在回收站或当前操作结果中核对。' }],
    [{ avid: '', pageIndex: null, title: 'trash-missing', message: 'Trash entry no longer exists.' }],
  ]);
});

it.each(['trash.move', 'trash.restore'] as const)('bounds %s failure details without truncating the total across restart', async operation => {
  const history = new TaskHistoryStore(file);
  const items = Array.from({ length: 22 }, (_, index) => ({ entryId: `trash-${index}`, avid: String(1000 + index),
    pageIndex: 2, succeeded: false, error: 'Access denied: ' + 'x'.repeat(1200) }));
  await history.start('many-failures', operation, {});
  await history.finish('many-failures', operation, operation === 'trash.move' ? {
    moved: [], failed: items.map(item => `${item.avid}:P2`),
    items: items.map(({ entryId: _entryId, ...item }) => item),
  } : { restored: [], failed: items.map(item => item.entryId), items });

  const reopened = await new TaskHistoryStore(file).snapshot();
  expect(reopened.warning).toBeUndefined();
  expect(reopened.entries[0]).toMatchObject({ status: 'failed', succeeded: 0, failed: 22 });
  expect(reopened.entries[0].failures).toHaveLength(20);
  expect(reopened.entries[0].failures[0]).toMatchObject({ avid: '1000', pageIndex: 2, title: 'av1000' });
  expect(reopened.entries[0].failures[19]).toMatchObject({ avid: '1019', pageIndex: 2, title: 'av1019' });
  expect(reopened.entries[0].failures[0].message).toHaveLength(1024);
  expect(reopened.entries[0].failures[0].message).toMatch(/^Access denied: .*…$/);
});

it('restores interrupted work as unknown and never invents a success or end time', async () => {
  const history = new TaskHistoryStore(file);
  await history.start('crash', 'trash.move', { rootPath: 'D:\\cache' });
  expect(JSON.parse(await readFile(file, 'utf8')).entries[0].status).toBe('running');
  const entry = (await new TaskHistoryStore(file).snapshot()).entries[0];
  expect(entry.status).toBe('unknown');
  expect(entry.recordedAt).toBeUndefined();
  expect(entry.summary).toContain('核对');
});

it('records cancellation with committed item counts and keeps exceptions uncertain', async () => {
  const history = new TaskHistoryStore(file);
  await history.start('restore', 'trash.restore', {});
  await history.finish('restore', 'trash.restore', { restored: ['one'], failed: ['two'], cancelled: true, unprocessed: ['three'] });
  await history.start('lost', 'export', {});
  await history.finish('lost', 'export', undefined, new Error('secret=must-not-save'));
  expect((await history.snapshot()).entries).toMatchObject([
    { id: 'lost', status: 'unknown', succeeded: 0 },
    { id: 'restore', status: 'cancelled', succeeded: 1, failed: 1, unprocessed: 1 },
  ]);
  expect(await readFile(file, 'utf8')).not.toContain('must-not-save');
});

it('does not expose unpublished output as a successful export', async () => {
  const history = new TaskHistoryStore(file);
  await history.start('cancel', 'export', {});
  await history.finish('cancel', 'export', { published: false, outputPath: null, exportedCount: 0, failures: [] });
  expect((await history.snapshot()).entries[0]).toMatchObject({ status: 'cancelled', succeeded: 0 });
  expect((await history.snapshot()).entries[0].outputPath).toBeUndefined();
  await history.start('invalid', 'play', {});
  await history.finish('invalid', 'play', { queued: 'not-a-number', failures: [] });
  expect((await history.snapshot()).entries[0].status).toBe('unknown');
});

it.each(['cancelled', 'CANCELLED'])('preserves confirmed cancellation (%s) instead of inventing an uncertain outcome', async code => {
  const history = new TaskHistoryStore(file);
  await history.start('cancelled', 'export', {});
  await history.finish('cancelled', 'export', undefined, Object.assign(new Error('Cancelled'), { code }));
  const entry = (await new TaskHistoryStore(file).snapshot()).entries[0];
  expect(entry.status).toBe('cancelled');
  expect(entry.summary).toContain('已取消');
});

it.each(['{broken', JSON.stringify({ schemaVersion: 2, entries: [] })])('preserves unreadable or future files verbatim', async original => {
  await writeFile(file, original);
  const history = new TaskHistoryStore(file);
  await history.start('local', 'play', {});
  await history.finish('local', 'play', { queued: 1, failures: [] });
  expect((await history.snapshot()).warning).toBeTruthy();
  expect((await history.snapshot()).entries[0].status).toBe('succeeded');
  expect(await readFile(file, 'utf8')).toBe(original);
});

it('keeps results in memory and warns when persistence fails', async () => {
  const parentFile = path.join(directory, 'not-a-directory');
  await writeFile(parentFile, 'preserve');
  const history = new TaskHistoryStore(path.join(parentFile, 'history.json'));
  await history.start('one', 'play', {});
  await history.finish('one', 'play', { queued: 1, failures: [] });
  expect((await history.snapshot()).warning).toBeTruthy();
  expect((await history.snapshot()).entries[0].status).toBe('succeeded');
  expect(await readFile(parentFile, 'utf8')).toBe('preserve');
});

it('serializes concurrent completions, bounds history, and expires old records', async () => {
  let now = Date.now();
  const history = new TaskHistoryStore(file, () => now);
  for (let index = 0; index < 103; index++) {
    await history.start(String(index), 'play', {});
    await history.finish(String(index), 'play', { queued: 1, failures: [] });
  }
  expect((await history.snapshot()).entries).toHaveLength(100);
  await Promise.all(['a', 'b'].map(id => history.start(id, 'play', {})));
  await Promise.all(['b', 'a'].map(id => history.finish(id, 'play', { queued: 2, failures: [] })));
  expect((await new TaskHistoryStore(file).snapshot()).entries.slice(0, 2)).toMatchObject([{ id: 'b', succeeded: 2 }, { id: 'a', succeeded: 2 }]);
  now += 31 * 24 * 60 * 60 * 1000;
  expect((await history.snapshot()).entries).toEqual([]);
  expect(JSON.parse(await readFile(file, 'utf8')).entries).toEqual([]);
});
