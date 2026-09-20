import { memo } from 'react';
import type { HostHealth, DesktopInfo } from '../../shared/contracts';
import type { Activity } from '../ui-types';
import { Icon } from '../components/Icon';
import { Empty, Description } from '../components/Common';

export const DiagnosticsPage = memo(function DiagnosticsPage({ health, desktop, activities, refresh, exportReport, busy }: { health: HostHealth | null; desktop: DesktopInfo | null; activities: Activity[]; refresh(): Promise<void>; exportReport(): Promise<void>; busy: boolean }) {
  return <div className="diagnostics-layout"><section className="card health-card"><div className={`health-orb ${health?.status === 'ok' ? 'ok' : ''}`}><Icon name={health?.status === 'ok' ? 'check' : 'warning'} /></div><div><h2>{health?.status === 'ok' ? '运行环境正常' : '需要检查运行环境'}</h2><p>{health?.warnings?.join('；') || '桌面壳与 .NET Desktop Host 之间的通信状态。'}</p></div><div className="toolbar"><button className="button secondary" onClick={() => void refresh()} disabled={busy}><Icon name="refresh" />重新检查</button><button className="button primary" onClick={() => void exportReport()} disabled={busy}><Icon name="export" />导出诊断</button></div></section>
    <section className="diagnostic-grid"><div className="card"><h3>桌面运行时</h3><Description rows={[['应用版本', desktop?.appVersion], ['Electron', desktop?.electronVersion], ['Chromium', desktop?.chromiumVersion], ['Node.js', desktop?.nodeVersion], ['平台', desktop ? `${desktop.platform}/${desktop.arch}` : undefined], ['显示后端', desktop?.displayBackend]]} /></div><div className="card"><h3>.NET Host</h3><Description rows={[['状态', health?.status], ['版本', health?.version], ['.NET', health?.runtime], ['平台', health?.platform], ['FFmpeg', health?.ffmpeg]]} /></div></section>
    <section className="card activity-card"><div className="panel-heading"><div><h2>本次运行记录</h2><span>仅保留当前会话最近 50 条</span></div></div>{activities.length ? <ul>{activities.map((item, index) => <li key={`${item.time.getTime()}-${index}`}><span className={`activity-dot ${item.kind}`} /><time>{item.time.toLocaleTimeString()}</time><p>{item.message}</p></li>)}</ul> : <Empty compact icon="diagnostics" title="暂无运行记录" body="扫描、播放、导出和维护结果会显示在这里。" />}</section>
  </div>;
});
