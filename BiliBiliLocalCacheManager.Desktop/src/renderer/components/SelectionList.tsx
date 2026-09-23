import { useEffect, useState } from 'react';
import { PageControls } from './Common';

export type SelectionListItem = { id: string; title: string; detail: string };
export function SelectionList({ items, label = '已选清单' }: { items: SelectionListItem[]; label?: string }) {
  const [offset, setOffset] = useState(0);
  const pageSize = 100;
  useEffect(() => setOffset(0), [items]);
  return <><ul className="result-list" aria-label={label}>{items.slice(offset, offset + pageSize).map(item =>
    <li key={item.id}><div><strong>{item.title}</strong><p>{item.detail}</p></div></li>)}</ul>
    <PageControls label={label} offset={offset} pageSize={pageSize} totalItems={items.length}
      hasMore={offset + pageSize < items.length} busy={false} onPage={setOffset} /></>;
}
