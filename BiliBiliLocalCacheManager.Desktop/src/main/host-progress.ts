import type { JsonValue } from '../shared/contracts';

const phases = new Set(['scan', 'copy', 'measure', 'prepare', 'probe', 'concat', 'mux', 'fallback']);

export class HostProgressTracker {
  #item = 0;
  #highest = new Map<string, number>();
  #global = new Map<string, number>();

  advance(value: JsonValue, method: string): boolean {
    if (!value || typeof value !== 'object' || Array.isArray(value) || value.operation !== method) return false;
    if (typeof value.stage !== 'string' || /wait|等待/i.test(value.stage)) return false;
    const details = value.details && typeof value.details === 'object' && !Array.isArray(value.details) ? value.details : {};
    const phase = typeof value.phase === 'string' && phases.has(value.phase) ? value.phase : undefined;
    if (value.phase != null && !phase) return false;
    const current = value.current;
    if (current != null && (typeof current !== 'number' || !Number.isSafeInteger(current) || current < 0)) return false;
    const counters = phase
      ? [value.percentage, details.processedSegmentDirectories, details.processedAvidDirectories, details.bytesCopied, details.processedSeconds]
      : [details.processedSegmentDirectories, details.processedAvidDirectories];
    if (counters.some(n => n != null && (typeof n !== 'number' || !Number.isFinite(n) || n < 0))) return false;
    if (typeof value.percentage === 'number' && value.percentage > 100) return false;
    const globalPhase = !phase || phase === 'scan' || phase === 'measure';
    if (!globalPhase && typeof current === 'number' && current < this.#item) return false;
    let advanced = false;
    if (!globalPhase && typeof current === 'number' && current > this.#item) {
      this.#item = current;
      this.#highest.clear();
      advanced = true;
    }
    if (globalPhase && typeof current === 'number' && current > (this.#global.get('current') ?? 0)) {
      this.#global.set('current', current);
      advanced = true;
    }
    // Keep each phase's high-water marks even when events switch back to an earlier phase.
    counters.forEach((n, index) => {
      const key = (phase ?? 'global') + ':' + index;
      const highest = globalPhase ? this.#global : this.#highest;
      if (typeof n === 'number' && n > (highest.get(key) ?? 0)) {
        highest.set(key, n);
        advanced = true;
      }
    });
    return advanced;
  }
}
