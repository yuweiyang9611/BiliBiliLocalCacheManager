// @vitest-environment jsdom
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { TaskHistoryStore } from '../../main/task-history';
import type { TaskHistoryEntry, TaskHistorySnapshot } from '../../shared/contracts';
import { TaskHistoryPanel } from './TaskHistoryPanel';

afterEach(cleanup);
const entry: TaskHistoryEntry = { id: 'one', operation: 'export', status: 'succeeded',
  startedAt: '2026-09-27T00:00:00Z', summary: '已发布 2 项。', succeeded: 2, failed: 0, unprocessed: 0, failures: [], outputPath: 'D:\\exports\\batch' };

it('keeps the latest history when an earlier read completes after a change event', async () => {
  let changed!: () => void;
  let resolveOld!: (value: TaskHistorySnapshot) => void;
  const old = new Promise<TaskHistorySnapshot>(resolve => { resolveOld = resolve; });
  const unsubscribe = vi.fn();
  Object.defineProperty(window, 'cacheManager', { configurable: true, value: {
    getTaskHistory: vi.fn().mockReturnValueOnce(old).mockResolvedValue({ entries: [entry] }),
    onTaskHistoryChanged: (listener: () => void) => { changed = listener; return unsubscribe; },
  } });
  const view = render(<TaskHistoryPanel />);
  act(() => changed());
  expect(await screen.findByText('已发布 2 项。')).toBeInTheDocument();
  await act(async () => resolveOld({ entries: [] }));
  expect(screen.getByText('D:\\exports\\batch')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /重试操作|重新导出/ })).not.toBeInTheDocument();
  view.unmount();
  expect(unsubscribe).toHaveBeenCalledOnce();
});

it('recovers a read failure and displays uncertain results without success counts', async () => {
  const getTaskHistory = vi.fn().mockRejectedValueOnce(new Error('private')).mockResolvedValue({
    warning: '记录仅保存在内存中。', entries: [{ ...entry, status: 'unknown', summary: '请核对输出', outputPath: undefined }],
  });
  Object.defineProperty(window, 'cacheManager', { configurable: true, value: {
    getTaskHistory, onTaskHistoryChanged: () => () => undefined,
  } });
  render(<TaskHistoryPanel />);
  expect(await screen.findByRole('alert')).toHaveTextContent('暂时无法读取任务历史');
  fireEvent.click(screen.getByRole('button', { name: '刷新任务历史' }));
  await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  expect(screen.getByText('结果待核对')).toBeInTheDocument();
  expect(screen.getByRole('status')).toHaveTextContent('记录仅保存在内存中');
  expect(screen.queryByText(/成功 2 项/)).not.toBeInTheDocument();
});

it('opens a retained batch by task ID and keeps the original path available when the folder has moved', async () => {
  const accessExportOutput = vi.fn().mockRejectedValueOnce(new Error('导出目录已移动或删除。')).mockResolvedValue(undefined);
  Object.defineProperty(window, 'cacheManager', { configurable: true, value: {
    getTaskHistory: vi.fn().mockResolvedValue({ entries: [entry] }), onTaskHistoryChanged: () => () => undefined,
    accessExportOutput,
  } });
  render(<TaskHistoryPanel />);
  fireEvent.click(await screen.findByText('导出'));
  fireEvent.click(screen.getByRole('button', { name: '打开导出目录' }));
  expect(await screen.findByText('导出目录已移动或删除。')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: '复制路径' }));
  expect(await screen.findByText('已复制导出路径。')).toBeInTheDocument();
  expect(accessExportOutput.mock.calls).toEqual([[entry.id, 'open'], [entry.id, 'copy']]);
});

it('shows which same-titled video and part failed final export verification after reopening history', async () => {
  const folder = await mkdtemp(path.join(tmpdir(), 'blcm-export-history-'));
  try {
    const file = path.join(folder, 'history.json');
    const history = new TaskHistoryStore(file);
    const sourceChanged = 'Source media or its prepared export changed before batch publication; retry the complete batch.';
    const stagedChanged = 'Staged export media changed before batch publication; retry the complete batch.';
    for (const [avid, message] of [['101', sourceChanged], ['202', stagedChanged]]) {
      await history.start(avid, 'export', {});
      await history.finish(avid, 'export', { published: false, outputPath: null, exportedCount: 0,
        failures: [{ avid, pageIndex: 2, title: '同名视频', message }] });
    }

    const reopened = new TaskHistoryStore(file);
    Object.defineProperty(window, 'cacheManager', { configurable: true, value: {
      getTaskHistory: () => reopened.snapshot(), onTaskHistoryChanged: () => () => undefined,
    } });
    render(<TaskHistoryPanel />);
    for (const summary of await screen.findAllByText('导出')) fireEvent.click(summary);
    const first = screen.getByText(sourceChanged);
    const second = screen.getByText(stagedChanged);
    expect(first).toBeVisible();
    expect(second).toBeVisible();
    expect(first.closest('li')).toHaveTextContent('同名视频 · av101 · P2');
    expect(second.closest('li')).toHaveTextContent('同名视频 · av202 · P2');
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});

it('highlights a cancelled export that left a staging directory to inspect after reopening history', async () => {
  const folder = await mkdtemp(path.join(tmpdir(), 'blcm-cancel-history-'));
  try {
    const file = path.join(folder, 'history.json');
    const history = new TaskHistoryStore(file);
    const warning = '导出已取消。导出暂存目录未能完全清理，请核对：D:\\exports\\.blcm-staging-1';
    await history.start('rollback', 'export', {});
    await history.finish('rollback', 'export', undefined, Object.assign(new Error(warning), { code: 'cancelled' }));
    await history.start('plain', 'play', {});
    await history.finish('plain', 'play', undefined, Object.assign(new Error('The operation was cancelled.'), { code: 'cancelled' }));

    const reopened = new TaskHistoryStore(file);
    Object.defineProperty(window, 'cacheManager', { configurable: true, value: {
      getTaskHistory: () => reopened.snapshot(), onTaskHistoryChanged: () => () => undefined,
    } });
    render(<TaskHistoryPanel />);
    fireEvent.click(await screen.findByText('导出'));
    fireEvent.click(screen.getByText('播放'));
    expect(screen.getAllByText('已取消')).toHaveLength(2);
    const detail = screen.getByText(warning);
    expect(detail).toBeVisible();
    expect(detail).toHaveClass('history-warning');
    expect(detail).toHaveTextContent('D:\\exports\\.blcm-staging-1');
    expect(screen.getAllByText('需要核对')).toHaveLength(1);
    expect(detail.closest('details')).toHaveTextContent('需要核对');
    expect(screen.getByText('任务已取消。')).not.toHaveClass('history-warning');
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});

it('does not flag ordinary structured trash cancellations for inspection after reopening history', async () => {
  const folder = await mkdtemp(path.join(tmpdir(), 'blcm-structured-cancel-'));
  try {
    const file = path.join(folder, 'history.json');
    const history = new TaskHistoryStore(file);
    await history.start('move', 'trash.move', {});
    await history.finish('move', 'trash.move', { moved: [], failed: [], cancelled: true, unprocessed: ['123:P2'] });
    await history.start('purge', 'trash.purge', {});
    await history.finish('purge', 'trash.purge', { purged: [], failed: [], cancelled: true, unprocessed: ['one'] });

    const reopened = new TaskHistoryStore(file);
    Object.defineProperty(window, 'cacheManager', { configurable: true, value: {
      getTaskHistory: () => reopened.snapshot(), onTaskHistoryChanged: () => () => undefined,
    } });
    const view = render(<TaskHistoryPanel />);
    fireEvent.click(await screen.findByText('移入回收站'));
    fireEvent.click(screen.getByText('永久删除'));
    expect(screen.getAllByText('已取消')).toHaveLength(2);
    const summaries = screen.getAllByText('已完成 0 项。');
    expect(summaries).toHaveLength(2);
    for (const summary of summaries) {
      expect(summary).toBeVisible();
      expect(summary).not.toHaveClass('history-warning');
    }
    expect(screen.queryByText('需要核对')).not.toBeInTheDocument();
    expect(view.container.querySelector('.history-warning')).toBeNull();
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});
