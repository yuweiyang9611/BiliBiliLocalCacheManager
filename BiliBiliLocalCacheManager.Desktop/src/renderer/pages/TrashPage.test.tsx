// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { TrashPage } from './TrashPage';

afterEach(cleanup);
it('distinguishes a trashed part from a whole video with the same title', () => {
  render(<TrashPage entries={[
    { id: 'whole', avid: '100', title: 'Video', sizeBytes: 1, deletedAt: null },
    { id: 'part', avid: '100', title: 'Video', sizeBytes: 1, deletedAt: null, pageIndex: 3 },
  ]} snapshot={null} selected={new Set()} setSelected={vi.fn()} pageTo={vi.fn()}
    refresh={vi.fn()} restore={vi.fn()} purge={vi.fn()} busy={false} inspectionDisabled={false} canPurge={false} />);
  expect(screen.getByText('整个视频')).toBeInTheDocument();
  expect(screen.getByText('P3')).toBeInTheDocument();
});
