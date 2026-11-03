import { useId, useLayoutEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

const layers: HTMLElement[] = [];
const previousInert = new Map<HTMLElement, boolean>();

function updateBackground() {
  const current = layers.at(-1);
  if (!current) {
    for (const [element, inert] of previousInert) element.toggleAttribute('inert', inert);
    previousInert.clear();
    return;
  }
  for (const element of document.body.children) {
    if (!(element instanceof HTMLElement)) continue;
    if (!previousInert.has(element)) previousInert.set(element, element.hasAttribute('inert'));
    element.toggleAttribute('inert', element !== current);
  }
}

function focusableElements(panel: HTMLElement) {
  return [...panel.querySelectorAll<HTMLElement>('button, [href], input, select, textarea, [tabindex]')]
    .filter(element => element.tabIndex >= 0 && !element.matches(':disabled') && !element.closest('[hidden], [inert]')
      && getComputedStyle(element).display !== 'none' && getComputedStyle(element).visibility !== 'hidden');
}

export function Modal({ title, onClose, children, closable = true }: {
  title: string; onClose(): void; children: ReactNode; closable?: boolean;
}) {
  const titleId = useId();
  const root = useRef<HTMLDivElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const current = useRef({ onClose, closable });
  useLayoutEffect(() => { current.current = { onClose, closable }; });
  useLayoutEffect(() => {
    const backdrop = root.current!;
    const dialog = panel.current!;
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    layers.push(backdrop);
    updateBackground();
    const focusFirst = () => {
      const elements = focusableElements(dialog);
      (elements.find(element => element.hasAttribute('data-modal-initial-focus')) ?? elements[0] ?? dialog).focus();
    };
    const keydown = (event: KeyboardEvent) => {
      if (layers.at(-1) !== backdrop) return;
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        if (current.current.closable) current.current.onClose();
      } else if (event.key === 'Tab') {
        const elements = focusableElements(dialog);
        const first = elements[0] ?? dialog;
        const last = elements.at(-1) ?? dialog;
        if (!dialog.contains(document.activeElement) || (event.shiftKey ? document.activeElement === first : document.activeElement === last)) {
          event.preventDefault();
          (event.shiftKey ? last : first).focus();
        }
      }
    };
    const focusin = (event: FocusEvent) => {
      if (layers.at(-1) === backdrop && !dialog.contains(event.target as Node)) focusFirst();
    };
    document.addEventListener('keydown', keydown, true);
    document.addEventListener('focusin', focusin);
    focusFirst();
    return () => {
      document.removeEventListener('keydown', keydown, true);
      document.removeEventListener('focusin', focusin);
      layers.splice(layers.indexOf(backdrop), 1);
      updateBackground();
      if (opener?.isConnected && !opener.closest('[inert]')) opener.focus();
    };
  }, []);
  return createPortal(<div ref={root} className="modal-backdrop" role="presentation"
    onMouseDown={event => { if (closable && event.target === event.currentTarget) onClose(); }}>
    <div ref={panel} className="modal" role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1}>
      <div className="modal-header"><h2 id={titleId}>{title}</h2>
        {closable && <button className="icon-button" aria-label="关闭" onClick={onClose}>×</button>}
      </div>{children}
    </div>
  </div>, document.body);
}
