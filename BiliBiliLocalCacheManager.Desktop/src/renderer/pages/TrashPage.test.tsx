// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { TrashPage } from './TrashPage';

afterEach(cleanup);
it('allows browsing and restoring when a snapshot is too large to purge', () => {
  const item = { id: 'item', avid: '1', title: 'Video', sizeBytes: 1, deletedAt: null, isRestorable: true };
  const snapshot = { snapshotToken: 'large', offset: 0, pageSize: 1, totalItems: 2, totalSizeBytes: 2,
    hasMore: true, canPurgeSnapshot: false, items: [item] };
  const restore = vi.fn(), pageTo = vi.fn(), purge = vi.fn();
  render(<TrashPage entries={[item]} snapshot={snapshot} selected={new Set(['item'])} setSelected={vi.fn()} pageTo={pageTo}
    refresh={vi.fn()} restore={restore} purge={purge} busy={false} inspectionDisabled={false} canPurge />);
  expect(screen.getByText(/条目标识总大小超过永久清空上限/)).toBeVisible();
  expect(screen.getByRole('button', { name: '清空回收站' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: '恢复所选' }));
  fireEvent.click(screen.getByRole('button', { name: /下一页/ }));
  expect(restore).toHaveBeenCalledOnce();
  expect(pageTo).toHaveBeenCalledWith(1);
  expect(purge).not.toHaveBeenCalled();
});

it('explains unavailable items and excludes them from restore selection while allowing full trash cleanup', () => {
  const entries = [
    { id: 'ready', avid: '100', title: 'Ready video', sizeBytes: 1, deletedAt: null, isRestorable: true },
    { id: 'blocked', avid: '200', title: 'Blocked video', sizeBytes: 1, deletedAt: null, isRestorable: false, unavailableReason: '正在永久清理' },
  ];
  const setSelected = vi.fn();
  const purge = vi.fn();
  const props = { entries, snapshot: null, selected: new Set(['blocked']), setSelected, purge, pageTo: vi.fn(),
    refresh: vi.fn(), restore: vi.fn(), busy: false, inspectionDisabled: false, canPurge: true };
  const view = render(<TrashPage {...props} />);
  expect(screen.getByText('正在永久清理')).toBeVisible();
  expect(screen.getByRole('checkbox', { name: '选择 Blocked video' })).toBeDisabled();
  expect(screen.getByRole('button', { name: '恢复所选' })).toBeDisabled();
  fireEvent.click(screen.getByRole('checkbox', { name: '选择本页可恢复条目' }));
  expect(setSelected).toHaveBeenCalledWith(new Set(['ready']));
  view.rerender(<TrashPage {...props} selected={new Set(['ready'])} />);
  expect(screen.getByRole('checkbox', { name: '选择本页可恢复条目' })).toBeChecked();
  expect(screen.getByRole('button', { name: '恢复所选' })).toBeEnabled();
  view.rerender(<TrashPage {...props} entries={[entries[1]]} />);
  expect(screen.getByRole('checkbox', { name: '选择本页可恢复条目' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: '清空回收站' }));
  expect(purge).toHaveBeenCalledWith(true);
});

it('distinguishes a trashed part from a whole video with the same title', () => {
  render(<TrashPage entries={[
    { id: 'whole', avid: '100', title: 'Video', sizeBytes: 1, deletedAt: null },
    { id: 'part', avid: '100', title: 'Video', sizeBytes: 1, deletedAt: null, pageIndex: 3 },
  ]} snapshot={null} selected={new Set()} setSelected={vi.fn()} pageTo={vi.fn()}
    refresh={vi.fn()} restore={vi.fn()} purge={vi.fn()} busy={false} inspectionDisabled={false} canPurge={false} />);
  expect(screen.getByText('整个视频')).toBeInTheDocument();
  expect(screen.getByText('P3')).toBeInTheDocument();
});
