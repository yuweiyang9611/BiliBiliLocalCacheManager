// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { RenderBoundary } from './RenderBoundary';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

function Broken(): never { throw new Error('private source path must not be displayed'); }

it('contains repeated page failures without exposing raw errors or reloading the app', () => {
  vi.spyOn(console, 'error').mockImplementation(() => undefined);
  render(<RenderBoundary scope="page"><Broken /></RenderBoundary>);
  expect(screen.getByRole('alert')).toHaveTextContent('当前页面显示异常');
  expect(screen.queryByText(/private source path/)).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: '重试显示此页' }));
  expect(screen.getByRole('alert')).toHaveTextContent('当前页面显示异常');
});

it('uses the main-process guard for root recovery and explains blocked or failed recovery', async () => {
  vi.spyOn(console, 'error').mockImplementation(() => undefined);
  const reloadWhenIdle = vi.fn().mockResolvedValueOnce(false).mockRejectedValueOnce(new Error('private error'));
  Object.defineProperty(window, 'cacheManager', { configurable: true, value: { reloadWhenIdle } });
  render(<RenderBoundary scope="app"><Broken /></RenderBoundary>);
  fireEvent.click(screen.getByRole('button', { name: '重新加载界面' }));
  expect(await screen.findByRole('status')).toHaveTextContent('后台任务或文件选择窗口尚未结束');
  fireEvent.click(screen.getByRole('button', { name: '重新加载界面' }));
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('暂时无法核对后台状态'));
  expect(reloadWhenIdle).toHaveBeenCalledTimes(2);
  expect(screen.queryByText(/private error/)).not.toBeInTheDocument();
});
