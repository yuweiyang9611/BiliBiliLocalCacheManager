// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Profiler } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import type { CacheEntry, CacheManagerApi, HostProgress } from '../../shared/contracts';
import { VirtualizedCacheTable } from './LibraryPage';
import { OperationProgress } from '../components/OperationProgress';

afterEach(cleanup);

it('offers keyboard details actions and preserves focus when navigating beyond mounted rows', () => {
  const items = Array.from({ length: 1000 }, (_, n): CacheEntry => ({ id: String(n), avid: String(n + 1), bvid: '', title: `Item ${n}`,
    ownerName: '', durationSeconds: 1, segmentCount: 1, sizeBytes: 1, isAllCompleted: true, lastUpdated: null }));
  const focus = vi.fn(), play = vi.fn();
  const { container } = render(<VirtualizedCacheTable items={items} selectedIds={new Set()} setSelectedIds={vi.fn()}
    focusedId={null} focus={focus} busy={false} play={play} />);
  const first = screen.getByRole('button', { name: '查看 Item 0 的分 P' });
  first.focus();
  fireEvent.keyDown(first, { key: 'End' });
  const last = screen.getByRole('button', { name: '查看 Item 999 的分 P' });
  expect(last).toHaveFocus();
  fireEvent.click(last);
  expect(focus).toHaveBeenLastCalledWith(items[999]);
  expect(play).not.toHaveBeenCalled();
  expect(container.querySelectorAll('[data-cache-row]').length).toBeLessThan(25);
  fireEvent.keyDown(last, { key: 'ArrowUp' });
  expect(screen.getByRole('button', { name: '查看 Item 998 的分 P' })).toHaveFocus();
  fireEvent.keyDown(document.activeElement!, { key: 'Home' });
  expect(screen.getByRole('button', { name: '查看 Item 0 的分 P' })).toHaveFocus();
});

it('keeps the mounted row count bounded while scrolling a large page', () => {
  const items = Array.from({ length: 1000 }, (_, n): CacheEntry => ({ id: String(n), avid: String(n + 1), bvid: '', title: `Item ${n}`,
    ownerName: '', durationSeconds: 1, segmentCount: 1, sizeBytes: 1, isAllCompleted: true, lastUpdated: null }));
  const { container, queryByText } = render(<VirtualizedCacheTable items={items} selectedIds={new Set()} setSelectedIds={vi.fn()}
    focusedId={null} focus={vi.fn()} busy={false} play={vi.fn()} />);
  const table = container.querySelector('[data-virtualized]')!;
  expect(container.querySelectorAll('[data-cache-row]').length).toBeLessThan(25);
  fireEvent.scroll(table, { target: { scrollTop: 5400 } });
  expect(queryByText('Item 0')).toBeNull();
  expect(queryByText('Item 100')).not.toBeNull();
  expect(container.querySelectorAll('[data-cache-row]').length).toBeLessThan(25);
});

it('isolates progress updates from the table render tree', () => {
  let report: (progress: HostProgress) => void = () => undefined;
  Object.defineProperty(window, 'cacheManager', { configurable: true, value: {
    onProgress: (listener: typeof report) => { report = listener; return () => undefined; },
  } as Pick<CacheManagerApi, 'onProgress'> });
  const rendered = vi.fn();
  render(<><Profiler id="table" onRender={rendered}><VirtualizedCacheTable items={[]} selectedIds={new Set()} setSelectedIds={vi.fn()}
    focusedId={null} focus={vi.fn()} busy={false} play={vi.fn()} /></Profiler><OperationProgress revision={0} /></>);
  rendered.mockClear();
  act(() => { for (let n = 0; n < 50; n++) report({ requestId: 'scan-1', operation: 'scan', stage: 'scan', percentage: n }); });
  expect(rendered).not.toHaveBeenCalled();
});
