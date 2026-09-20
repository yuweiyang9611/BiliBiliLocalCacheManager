import { memo } from 'react';
import type { AppSettings, StorageSnapshot } from '../../shared/contracts';
import { Icon } from '../components/Icon';
import { Metric } from '../components/Common';
import { formatBytes } from '../display';

export const StoragePage = memo(function StoragePage({ storage, settings, refresh, cleanup, clear, open, busy, inspectionDisabled }: { storage: StorageSnapshot; settings: AppSettings; refresh(): Promise<void>; cleanup(): Promise<void>; clear(): void; open(): Promise<void>; busy: boolean; inspectionDisabled: boolean }) {
  const max = Math.max(storage.originalCache.bytes, storage.transcodeCache.bytes, storage.trash.bytes, 1);
  return <div className="stack"><section className="metric-grid"><Metric label="原始缓存" value={formatBytes(storage.originalCache.bytes)} detail={`${storage.originalCache.itemCount} 项`} color="blue" /><Metric label="转码缓存" value={formatBytes(storage.transcodeCache.bytes)} detail={`${storage.transcodeCache.itemCount} 项`} color="violet" /><Metric label="应用回收站" value={formatBytes(storage.trash.bytes)} detail={`${storage.trash.itemCount} 项`} color="amber" /><Metric label="合计占用" value={formatBytes(storage.totalBytes)} detail="由应用管理" color="green" /></section>
    <section className="card storage-chart"><div className="panel-heading"><div><h2>空间分布</h2><span>{storage.lastMaintenanceSummary ?? '最近没有自动维护记录'}</span></div><button className="button secondary" onClick={() => void refresh()} disabled={inspectionDisabled}><Icon name="refresh" />刷新统计</button></div>
      {[['B 站原始缓存', storage.originalCache.bytes, 'blue'], ['转码缓存', storage.transcodeCache.bytes, 'violet'], ['应用回收站', storage.trash.bytes, 'amber']].map(([label, bytes, color]) => <div className="bar-row" key={label as string}><div><span>{label}</span><b>{formatBytes(bytes as number)}</b></div><div className="bar-track"><i className={color as string} style={{ width: `${Math.max(2, (bytes as number) / max * 100)}%` }} /></div></div>)}
    </section><section className="card policy-card"><div><h2>转码缓存策略</h2><p>超过 {settings.transcodeCacheRetentionDays} 天或总量超过 {settings.transcodeCacheMaxSizeGigabytes} GB 时进行维护。可在“设置”中调整。</p></div><div className="toolbar"><button className="button ghost" onClick={() => void open()} disabled={inspectionDisabled}><Icon name="folder" />打开转码缓存目录</button><button className="button secondary" onClick={() => void cleanup()} disabled={busy}><Icon name="refresh" />按策略清理</button><button className="button danger" onClick={clear} disabled={busy}><Icon name="delete" />清空转码缓存</button></div></section></div>;
});
