import { memo } from 'react';
import type { TrashEntry, TrashPage as TrashPageResult } from '../../shared/contracts';
import { Icon } from '../components/Icon';
import { Empty, PageControls } from '../components/Common';
import { toggleSet, formatBytes, formatDate } from '../display';

interface TrashPageProps {
  entries: TrashEntry[];
  snapshot: TrashPageResult | null;
  pageTo(offset: number): void;
  selected: Set<string>;
  setSelected(value: Set<string>): void;
  refresh(): Promise<void>;
  restore(): void;
  purge(all: boolean): void;
  busy: boolean;
  inspectionDisabled: boolean;
  canPurge: boolean;
}

export const TrashPage = memo(function TrashPage({
  entries, snapshot, pageTo, selected, setSelected, refresh, restore, purge, busy, inspectionDisabled, canPurge,
}: TrashPageProps) {
  const available = entries.filter(entry => entry.isRestorable !== false);
  const size = snapshot?.totalSizeBytes ?? entries.reduce((sum, entry) => sum + entry.sizeBytes, 0);
  return <section className="card full-panel">
    <div className="panel-heading">
      <div><h2>应用回收站</h2><span>{snapshot?.totalItems ?? entries.length} 项 · {formatBytes(size)}{!canPurge && ' · 当前平台暂不支持永久清理'}</span></div>
      <div className="toolbar">
        <button className="button ghost" onClick={() => void refresh()} disabled={inspectionDisabled}><Icon name="refresh" />刷新</button>
        <button className="button secondary" onClick={restore} disabled={busy || !available.some(entry => selected.has(entry.id))}><Icon name="restore" />恢复所选</button>
        {canPurge && <button className="button danger" onClick={() => purge(true)} disabled={!entries.length || busy}>清空回收站</button>}
      </div>
    </div>
    {entries.length ? <div className="table-scroll"><table>
      <thead><tr>
        <th className="check-cell"><input aria-label="选择本页可恢复条目" type="checkbox"
          disabled={!available.length} checked={available.length > 0 && available.every(entry => selected.has(entry.id))}
          onChange={event => setSelected(event.target.checked ? new Set(available.map(entry => entry.id)) : new Set())} /></th>
        <th>标题</th><th>AV 号</th><th>大小</th><th>删除时间</th><th>原位置</th><th>恢复状态</th>
      </tr></thead>
      <tbody>{entries.map(item => <tr key={item.id}>
        <td className="check-cell"><input aria-label={'选择 ' + item.title} type="checkbox"
          disabled={item.isRestorable === false} checked={item.isRestorable !== false && selected.has(item.id)}
          onChange={() => setSelected(toggleSet(selected, item.id))} /></td>
        <td><strong>{item.title || '未命名缓存'}</strong><small>{item.pageIndex == null ? '整个视频' : 'P' + item.pageIndex}</small></td>
        <td>av{item.avid}</td><td>{formatBytes(item.sizeBytes)}</td><td>{formatDate(item.deletedAt)}</td>
        <td className="path-cell" title={item.originalPath}>{item.originalPath ?? '—'}</td>
        <td>{item.isRestorable === false
          ? <><strong>不可恢复</strong><small>{item.unavailableReason}</small></>
          : item.isRestorable ? '可恢复' : '恢复时核验'}</td>
      </tr>)}</tbody>
    </table></div> : <Empty icon="trash" title="回收站为空" body="从缓存库删除的项目会先移到这里，避免误删。" />}
    {snapshot && <PageControls label="回收站" {...snapshot} busy={inspectionDisabled} onPage={pageTo} />}
  </section>;
});
