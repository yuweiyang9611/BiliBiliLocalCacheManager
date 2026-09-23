import { useState } from 'react';
import { Modal } from './Common';
import { SelectionList, type SelectionListItem } from './SelectionList';

export type Confirmation = {
  title: string;
  body: string;
  destructive?: boolean;
  requiredText?: string;
  items?: SelectionListItem[];
  action(text: string): void;
};

export function ConfirmationDialog({ value, close }: { value: Confirmation; close(): void }) {
  const [text, setText] = useState('');
  return <Modal title={value.title} onClose={close}>
    <p>{value.body}</p>
    {value.items && <SelectionList items={value.items} label="操作范围" />}
    {value.requiredText && <label className="field">输入确认文字：{value.requiredText}
      <input aria-label="输入确认文字" autoComplete="off" value={text} onChange={event => setText(event.target.value)} />
    </label>}
    <div className="modal-actions"><button className="button ghost" onClick={close}>取消</button>
      <button className={value.destructive ? 'button danger' : 'button primary'} disabled={Boolean(value.requiredText && text !== value.requiredText)}
        onClick={() => { close(); value.action(text); }}>确认</button>
    </div>
  </Modal>;
}
