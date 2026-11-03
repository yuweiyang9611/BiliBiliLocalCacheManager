import type { OperationState } from '../shared/contracts';
import { cacheManager } from './cache-manager';

// Subscribe before reading the snapshot, then overlay live events (including
// terminal events) so a delayed snapshot cannot resurrect a completed task.
export function observeOperationStates(onChange: (states: Record<string, OperationState>) => void) {
  let disposed = false;
  let restoring = true;
  let states: Record<string, OperationState> = {};
  const updates = new Map<string, OperationState>();
  const apply = (target: Record<string, OperationState>, value: OperationState) => {
    if (value.operation === 'search' || value.operation === 'cache.details') return;
    if (value.state === 'settled') delete target[value.requestId];
    else target[value.requestId] = value;
  };
  const unsubscribe = cacheManager.onOperationState(value => {
    if (disposed) return;
    if (value.operation === 'search' || value.operation === 'cache.details') return;
    if (restoring) updates.set(value.requestId, value);
    states = { ...states };
    apply(states, value);
    onChange(states);
  });
  const ready = (async () => {
    try {
      const snapshot = await cacheManager.getOperationStates();
      if (disposed) return;
      const restored = {};
      for (const value of [...snapshot, ...updates.values()]) apply(restored, value);
      states = restored;
      onChange(states);
    } finally {
      restoring = false;
      updates.clear();
    }
  })();
  return { ready, dispose() { disposed = true; updates.clear(); unsubscribe(); } };
}
