import { memo } from 'react';
import type { AppSettings, PlayerPreference } from '../../shared/contracts';
import { Icon } from '../components/Icon';
import { Check } from '../components/Common';

export const SettingsPage = memo(function SettingsPage({ value, edit, canSave, browse, save, busy }: {
  value: AppSettings; edit(patch: Partial<AppSettings>): void; canSave: boolean;
  browse(): Promise<void>; save(): Promise<void>; busy: boolean;
}) {
  const change = <K extends keyof AppSettings>(key: K, next: AppSettings[K]) => edit({ [key]: next });
  return <div className="settings-layout"><section className="card settings-section"><div className="section-title"><h2>缓存与扫描</h2><p>设置默认目录和扫描行为。</p></div><div className="settings-fields"><label>缓存根目录<div className="input-action"><input disabled={busy} value={value.rootPath} onChange={(event) => change('rootPath', event.target.value)} /><button className="icon-button" onClick={() => void browse()} aria-label="选择缓存根目录" disabled={busy}><Icon name="folder" /></button></div></label><Check label="记住缓存目录" checked={value.rememberRootPath} disabled={busy || !canSave} onChange={(next) => edit({ rememberRootPath: next, scanOnStartup: next && value.scanOnStartup })} /><Check label="启动时自动扫描记住的目录" checked={value.scanOnStartup} disabled={busy || !canSave || !value.rememberRootPath} onChange={(next) => edit({ rememberRootPath: value.rememberRootPath || next, scanOnStartup: next })} /><Check label="扫描时包含下载未完成的缓存" checked={value.includeIncomplete} disabled={busy} onChange={(next) => change('includeIncomplete', next)} /></div></section>
    <section className="card settings-section"><div className="section-title"><h2>播放与导出</h2><p>选择外部播放器；导出由 FFmpeg 后端完成。</p></div><div className="settings-fields"><label>首选播放器<select disabled={busy || !canSave} value={value.playerPreference} onChange={(event) => change('playerPreference', event.target.value as PlayerPreference)}><option value="system">系统默认</option><option value="mpv">mpv</option><option value="vlc">VLC</option></select></label></div></section>
    <section className="card settings-section"><div className="section-title"><h2>转码缓存维护</h2><p>限制临时播放与导出产物的保留周期和总量。</p></div><div className="settings-fields inline-fields"><label>保留天数<input disabled={busy || !canSave} type="number" min="1" max="1825" value={value.transcodeCacheRetentionDays} onChange={(event) => change('transcodeCacheRetentionDays', Number(event.target.value))} /><small>1–1825 天</small></label><label>容量上限<input disabled={busy || !canSave} type="number" min="1" max="128" value={value.transcodeCacheMaxSizeGigabytes} onChange={(event) => change('transcodeCacheMaxSizeGigabytes', Number(event.target.value))} /><small>1–128 GB</small></label></div></section>
    <div className="settings-actions">{!canSave && <p>设置文件只读，缓存目录和扫描选项仅在本次会话使用。</p>}<button className="button primary" disabled={busy} onClick={() => void save()}>{canSave ? '保存设置' : '应用到本次会话'}</button></div></div>;
});
