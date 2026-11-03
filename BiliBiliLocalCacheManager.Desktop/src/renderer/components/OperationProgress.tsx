import { memo, useEffect, useState } from 'react';
import type { HostProgress } from '../../shared/contracts';
import { cacheManager } from '../cache-manager';

export const OperationProgress = memo(function OperationProgress({ revision }: { revision: number }) {
  const [progress, setProgress] = useState<HostProgress | null>(null);
  useEffect(() => cacheManager.onProgress(setProgress), []);
  useEffect(() => setProgress(null), [revision]);
  if (!progress) return null;
  const percentage = typeof progress.percentage === 'number' && Number.isFinite(progress.percentage)
    ? Math.max(0, Math.min(100, progress.percentage)) : undefined;
  const label = progressLabel(progress);
  return <div className="operation-progress" role="progressbar" aria-label={label}
    aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentage}>
    <div className={percentage === undefined ? 'indeterminate' : undefined} style={percentage === undefined ? undefined : { width: `${percentage}%` }} />
    <span title={progress.message}>{label}</span>
  </div>;
});

function progressLabel(progress: HostProgress): string {
  if (progress.operation === 'scan') {
    if (progress.stage === 'completed') return '扫描完成';
    const count = typeof progress.current === 'number' && Number.isSafeInteger(progress.current) && progress.current >= 0
      ? ` · 已处理 ${progress.current.toLocaleString('zh-CN')}` : null;
    if (progress.stage === 'verifying') return `正在校验分 P 身份${count === null ? '' : `${count} 个分 P`}`;
    if (progress.stage === 'scanning') return `正在扫描缓存${count === null ? '' : `${count} 个视频目录`}`;
  }
  return progress.message ?? progress.stage;
}
