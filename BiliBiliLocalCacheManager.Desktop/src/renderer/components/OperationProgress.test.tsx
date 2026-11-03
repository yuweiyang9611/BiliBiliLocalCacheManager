// @vitest-environment jsdom
import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { HostProgress } from '../../shared/contracts';
import { OperationProgress } from './OperationProgress';

afterEach(cleanup);

it('shows scan identity counts without inventing a completion percentage', () => {
  let report!: (value: HostProgress) => void;
  const unsubscribe = vi.fn();
  Object.defineProperty(window, 'cacheManager', { configurable: true, value: {
    onProgress: (listener: typeof report) => { report = listener; return unsubscribe; },
  } });
  const view = render(<OperationProgress revision={0} />);
  act(() => report({ requestId: 'scan-1', operation: 'scan', stage: 'verifying', phase: 'measure', current: 1250 }));
  const bar = screen.getByRole('progressbar', { name: '正在校验分 P 身份 · 已处理 1,250 个分 P' });
  expect(bar).not.toHaveAttribute('aria-valuenow');
  act(() => report({ requestId: 'scan-1', operation: 'scan', stage: 'completed', percentage: 100 }));
  expect(screen.getByRole('progressbar', { name: '扫描完成' })).toHaveAttribute('aria-valuenow', '100');
  view.rerender(<OperationProgress revision={1} />);
  expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  view.unmount();
  expect(unsubscribe).toHaveBeenCalledOnce();
});
