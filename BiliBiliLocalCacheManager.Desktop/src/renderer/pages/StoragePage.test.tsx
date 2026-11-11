// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { defaultSettings, emptyStorage } from '../../shared/contracts';
import { StoragePage } from './StoragePage';

afterEach(cleanup);
it('shows the policy estimate separately from trash, including a measured zero and an unavailable preview', () => {
  const storage = { ...emptyStorage, trash: { bytes: 2 * 1024 ** 3, itemCount: 1 }, totalBytes: 2 * 1024 ** 3 };
  const props = { settings: defaultSettings, refresh: vi.fn(), cleanup: vi.fn(), clear: vi.fn(), open: vi.fn(),
    busy: false, inspectionDisabled: false };
  const view = render(<StoragePage {...props} storage={{ ...storage, policyReclaimableBytes: 0 }} />);
  expect(screen.getByText('按策略清理预计释放 0 MB')).toBeVisible();
  view.rerender(<StoragePage {...props} storage={{ ...storage, policyReclaimableBytes: 128 * 1024 ** 2 }} />);
  expect(screen.getByText('按策略清理预计释放 128.0 MB')).toBeVisible();
  view.rerender(<StoragePage {...props} storage={storage} />);
  expect(screen.getByText('按策略清理预计释放空间暂不可用')).toBeVisible();
});
