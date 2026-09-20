import { memo, useEffect, useState } from 'react';
import type { HostProgress } from '../../shared/contracts';
import { cacheManager } from '../cache-manager';

export const OperationProgress = memo(function OperationProgress({ revision }: { revision: number }) {
  const [progress, setProgress] = useState<HostProgress | null>(null);
  useEffect(() => cacheManager.onProgress(setProgress), []);
  useEffect(() => setProgress(null), [revision]);
  if (!progress) return null;
  return <div className="operation-progress"><div style={{ width: `${Math.max(2, Math.min(100, progress.percentage ?? 12))}%` }} /><span>{progress.message ?? progress.stage}</span></div>;
});
