import { useEffect, useRef, useState } from 'react';
import type { TaskHistorySnapshot, TaskOperation, TaskOutcome } from '../../shared/contracts';
import { cacheManager } from '../cache-manager';
import { ExportOutputActions } from './ExportOutputActions';

const names: Record<TaskOperation, string> = { play: '播放', export: '导出', 'trash.move': '移入回收站',
  'trash.restore': '恢复缓存', 'trash.purge': '永久删除', 'trash.purgeSnapshot': '清空回收站' };
const outcomes: Record<TaskOutcome, string> = { running: '进行中', succeeded: '成功', partial: '部分成功',
  failed: '失败', cancelled: '已取消', confirmation: '待确认转码', unknown: '结果待核对' };

export function TaskHistoryPanel() {
  const [snapshot, setSnapshot] = useState<TaskHistorySnapshot | null>(null);
  const [error, setError] = useState(false);
  const refresh = useRef<() => void>(() => undefined);
  useEffect(() => {
    let disposed = false;
    let revision = 0;
    const load = async () => {
      const request = ++revision;
      try {
        const value = await cacheManager.getTaskHistory();
        if (!disposed && request === revision) { setSnapshot(value); setError(false); }
      } catch { if (!disposed && request === revision) setError(true); }
    };
    refresh.current = () => { void load(); };
    const unsubscribe = cacheManager.onTaskHistoryChanged(refresh.current);
    void load();
    return () => { disposed = true; unsubscribe(); };
  }, []);
  return <section className="card task-history" aria-labelledby="task-history-title">
    <div className="panel-heading"><div><h2 id="task-history-title">任务历史</h2><span>保留最近 30 天内的 100 项任务，重启后可查看</span></div>
      <button className="button ghost" onClick={() => refresh.current()}>刷新任务历史</button></div>
    <p className="history-description">记录播放交接、导出和回收站操作。路径保存在本机；历史记录不会自动重试操作。</p>
    {error && <p role="alert">暂时无法读取任务历史，请重试。</p>}
    {snapshot?.warning && <p role="status">{snapshot.warning}</p>}
    {!snapshot && !error && <p role="status">正在读取任务历史…</p>}
    {snapshot?.entries.length === 0 && <p>暂无任务记录。之后执行的播放、导出和回收站操作会显示在这里。</p>}
    {snapshot?.entries.map(entry => <details key={entry.id} className="history-entry">
      <summary><strong>{names[entry.operation]}</strong><span>{outcomes[entry.status]}</span>
        <time dateTime={entry.startedAt}>{new Date(entry.startedAt).toLocaleString('zh-CN')}</time></summary>
      <p>{entry.summary}</p>
      {entry.status !== 'running' && entry.status !== 'unknown' && <p>成功 {entry.succeeded} 项 · 失败 {entry.failed} 项 · 未处理 {entry.unprocessed} 项</p>}
      {entry.rootPath && <p>缓存目录：<code>{entry.rootPath}</code></p>}
      {entry.operation === 'export' && entry.status === 'succeeded' && entry.succeeded > 0 && entry.outputPath &&
        <ExportOutputActions taskId={entry.id} outputPath={entry.outputPath} />}
      {entry.failures.length > 0 && <ul>{entry.failures.map((failure, index) => <li key={index}>
        <strong>{failure.title || failure.avid}{failure.pageIndex !== null ? ` · P${failure.pageIndex}` : ''}</strong><p>{failure.message}</p>
      </li>)}</ul>}
      {entry.failed > entry.failures.length && <p>仅保留前 {entry.failures.length} 项失败明细，失败总数为 {entry.failed} 项。</p>}
    </details>)}
  </section>;
}
