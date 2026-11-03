// @vitest-environment jsdom
import { useState } from 'react';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { Modal } from './Common';
import { ConfirmationDialog } from './ConfirmationDialog';

afterEach(cleanup);

it('moves focus into the modal, contains keyboard focus and returns it to the opener', () => {
  function Example() {
    const [open, setOpen] = useState(false);
    return <><button onClick={() => setOpen(true)}>打开清单</button><button>后台操作</button>
      {open && <Modal title="已选清单" onClose={() => setOpen(false)}><button>最后一项</button></Modal>}</>;
  }
  const { container } = render(<Example />);
  const opener = screen.getByRole('button', { name: '打开清单' });
  opener.focus();
  fireEvent.click(opener);
  const dialog = screen.getByRole('dialog', { name: '已选清单' });
  const first = within(dialog).getByRole('button', { name: '关闭' });
  const last = within(dialog).getByRole('button', { name: '最后一项' });
  expect(first).toHaveFocus();
  expect(container).toHaveAttribute('inert');
  last.focus();
  fireEvent.keyDown(last, { key: 'Tab' });
  expect(first).toHaveFocus();
  fireEvent.keyDown(first, { key: 'Tab', shiftKey: true });
  expect(last).toHaveFocus();
  fireEvent.keyDown(last, { key: 'Escape' });
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  expect(container).not.toHaveAttribute('inert');
  expect(opener).toHaveFocus();
});

it('initially focuses cancellation for a destructive confirmation', () => {
  render(<ConfirmationDialog value={{ title: '删除缓存', body: '确认范围', destructive: true, action() {} }} close={() => undefined} />);
  expect(screen.getByRole('button', { name: '取消' })).toHaveFocus();
});

it('keeps a required choice open on Escape and redirects attempted background focus', () => {
  const close = vi.fn();
  render(<><button>后台按钮</button><Modal title="确认旧版缓存目录" closable={false} onClose={close}>
    <button>忘记目录</button><button>仅记住，不扫描</button>
  </Modal></>);
  const first = screen.getByRole('button', { name: '忘记目录' });
  expect(first).toHaveFocus();
  screen.getByRole('button', { name: '后台按钮' }).focus();
  expect(first).toHaveFocus();
  fireEvent.keyDown(first, { key: 'Escape' });
  expect(close).not.toHaveBeenCalled();
  expect(screen.getByRole('dialog', { name: '确认旧版缓存目录' })).toBeInTheDocument();
});

it('restores the underlying dialog and its focus after a nested dialog closes', () => {
  function Example() {
    const [child, setChild] = useState(false);
    return <Modal title="第一层" onClose={() => undefined}>
      <button onClick={() => setChild(true)}>打开第二层</button>
      {child && <Modal title="第二层" onClose={() => setChild(false)}><button>第二层操作</button></Modal>}
    </Modal>;
  }
  const { container, unmount } = render(<Example />);
  const opener = screen.getByRole('button', { name: '打开第二层' });
  opener.focus(); fireEvent.click(opener);
  const underlying = screen.getByRole('dialog', { name: '第一层' }).parentElement!;
  expect(underlying).toHaveAttribute('inert');
  fireEvent.keyDown(within(screen.getByRole('dialog', { name: '第二层' })).getByRole('button', { name: '关闭' }), { key: 'Escape' });
  expect(underlying).not.toHaveAttribute('inert');
  expect(container).toHaveAttribute('inert');
  expect(opener).toHaveFocus();
  unmount();
  expect(container).not.toHaveAttribute('inert');
});
