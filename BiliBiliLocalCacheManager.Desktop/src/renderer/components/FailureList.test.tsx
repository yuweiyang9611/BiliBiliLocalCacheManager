// @vitest-environment jsdom
import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { FailureList } from './FailureList';

afterEach(cleanup);

it('shows each failure reason next to its available video and part identifiers', () => {
  render(<FailureList failures={[
    { title: '同名视频', avid: '101', pageIndex: 2, message: '源媒体已变化' },
    { title: '同名视频', avid: '202', pageIndex: 2, message: '暂存文件已变化' },
    { title: 'av303', avid: '303', pageIndex: null, message: '文件不可读' },
    { title: '', avid: '', pageIndex: null, message: '批次目录不可用' },
  ]} />);
  const rows = screen.getAllByRole('listitem').map(row => within(row));
  for (const [index, [label, reason]] of [
    ['同名视频 · av101 · P2', '源媒体已变化'], ['同名视频 · av202 · P2', '暂存文件已变化'],
    ['av303', '文件不可读'], ['操作失败', '批次目录不可用'],
  ].entries()) {
    expect(rows[index].getByText(label)).toBeVisible();
    expect(rows[index].getByText(reason)).toBeVisible();
  }
});
