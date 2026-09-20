import type { ReactNode } from 'react';
import { Icon, type IconName } from './Icon';
import type { CachePageState } from '../ui-types';

export function Metric({ label, value, detail, color }: { label: string; value: string; detail: string; color: string }) { return <div className={`metric-card ${color}`}><span>{label}</span><strong>{value}</strong><small>{detail}</small></div>; }

export function Check({ label, checked, disabled = false, onChange }: { label: string; checked: boolean; disabled?: boolean; onChange(value: boolean): void }) { return <label className="check"><input type="checkbox" checked={checked} disabled={disabled} onChange={(event) => onChange(event.target.checked)} /><span>{label}</span></label>; }

export function Empty({ icon, title, body, compact = false }: { icon: IconName; title: string; body: string; compact?: boolean }) { return <div className={compact ? 'empty compact' : 'empty'}><Icon name={icon} /><h3>{title}</h3><p>{body}</p></div>; }

export function Modal({ title, onClose, children, closable = true }: { title: string; onClose(): void; children: ReactNode; closable?: boolean }) { return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (closable && event.target === event.currentTarget) onClose(); }}><div className="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title"><div className="modal-header"><h2 id="modal-title">{title}</h2>{closable && <button className="icon-button" aria-label="关闭" onClick={onClose}>×</button>}</div>{children}</div></div>; }

export function Description({ rows }: { rows: Array<[string, string | undefined]> }) { return <dl>{rows.map(([key, value]) => <div key={key}><dt>{key}</dt><dd>{value || '—'}</dd></div>)}</dl>; }

export function PageControls({ label, offset, pageSize, totalItems, hasMore, busy, onPage }: CachePageState & { label: string; busy: boolean; onPage(offset: number): void }) {
  if (totalItems <= pageSize && offset === 0) return null;
  const start = totalItems === 0 ? 0 : offset + 1;
  const end = Math.min(totalItems, offset + pageSize);
  return <div className="page-controls" aria-label={`${label}分页`}>
    <span>{start}–{end} / {totalItems}</span>
    <div><button className="button ghost" disabled={busy || offset === 0} onClick={() => onPage(Math.max(0, offset - pageSize))}>上一页</button><button className="button ghost" disabled={busy || !hasMore} onClick={() => onPage(offset + pageSize)}>下一页</button></div>
  </div>;
}
