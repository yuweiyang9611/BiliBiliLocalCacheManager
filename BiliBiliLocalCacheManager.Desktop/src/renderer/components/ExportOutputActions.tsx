import { useState } from 'react';
import { cacheManager } from '../cache-manager';

export function ExportOutputActions({ taskId, outputPath }: { taskId?: string; outputPath: string }) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const access = async (action: 'open' | 'copy') => {
    if (!taskId || busy) return;
    setBusy(true);
    setMessage('');
    try {
      await cacheManager.accessExportOutput(taskId, action);
      setMessage(action === 'open' ? '已打开导出目录。' : '已复制导出路径。');
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '操作失败，请稍后重试。');
    } finally { setBusy(false); }
  };
  return <div className="export-output">
    <p>已发布到：<code>{outputPath}</code></p>
    {taskId && <div className="toolbar">
      <button className="button secondary" disabled={busy} onClick={() => void access('open')}>打开导出目录</button>
      <button className="button ghost" disabled={busy} onClick={() => void access('copy')}>复制路径</button>
    </div>}
    {message && <p role="status">{message}</p>}
  </div>;
}
