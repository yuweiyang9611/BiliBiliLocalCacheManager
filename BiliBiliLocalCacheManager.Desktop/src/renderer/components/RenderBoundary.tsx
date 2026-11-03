import { Component, useRef, useState, type ReactNode } from 'react';
import { cacheManager } from '../cache-manager';

export function RendererRecovery({ label = '重新加载界面' }: { label?: string }) {
  const inFlight = useRef(false);
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState('');
  const recover = async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    setPending(true);
    setMessage('');
    try {
      if (!await cacheManager.reloadWhenIdle()) {
        setMessage('后台任务或文件选择窗口尚未结束。请等待完成后再试，避免重复执行操作。');
      }
    } catch {
      setMessage('暂时无法核对后台状态。请先核对输出文件或缓存状态，再关闭并重新打开应用。');
    } finally {
      inFlight.current = false;
      setPending(false);
    }
  };
  return <>
    <button className="button primary" disabled={pending} onClick={() => void recover()}>{pending ? '正在核对后台状态…' : label}</button>
    {message && <p role="status">{message}</p>}
  </>;
}

// Page recovery keeps App (and its in-flight operation guards) mounted. Root
// recovery goes through the main process; never remount App over a live request.
export class RenderBoundary extends Component<{
  children: ReactNode;
  scope: 'page' | 'app';
}, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() { return { failed: true }; }

  render() {
    if (!this.state.failed) return this.props.children;
    const page = this.props.scope === 'page';
    return <section className={`render-failure${page ? '' : ' render-failure-root'}`} role="alert" aria-labelledby="render-failure-title">
      <h1 id="render-failure-title">{page ? '当前页面显示异常' : '界面暂时无法显示'}</h1>
      <p>{page ? '后台任务仍由应用跟踪。可以重试显示此页，或切换到其他页面。' : '后台任务可能仍在执行。重新加载前，应用会先核对任务状态。'}</p>
      <p>界面恢复不会自动重试播放、导出或删除。恢复后请核对操作结果。</p>
      {page
        ? <button className="button secondary" onClick={() => this.setState({ failed: false })}>重试显示此页</button>
        : <RendererRecovery />}
    </section>;
  }
}
