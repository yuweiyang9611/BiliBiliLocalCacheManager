import { useEffect, useLayoutEffect, useRef } from 'react';

export function useShortcuts(handler: (event: KeyboardEvent) => void) {
  const current = useRef(handler);
  useLayoutEffect(() => { current.current = handler; });
  useEffect(() => {
    const listener = (event: KeyboardEvent) => current.current(event);
    window.addEventListener('keydown', listener);
    return () => window.removeEventListener('keydown', listener);
  }, []);
}
