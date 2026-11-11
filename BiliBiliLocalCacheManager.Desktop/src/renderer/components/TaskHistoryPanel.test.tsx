// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
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
