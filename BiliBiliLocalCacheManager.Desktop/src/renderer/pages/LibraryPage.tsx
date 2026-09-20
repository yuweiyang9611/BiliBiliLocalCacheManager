import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { AppSettings, CacheEntry, CacheDetails, CacheSegment, SelectionTarget } from '../../shared/contracts';
import type { CachePageState } from '../ui-types';
import { Icon } from '../components/Icon';
import { Check, Empty, PageControls } from '../components/Common';
import { toggleSet, formatBytes, formatDuration, formatDate } from '../display';

export interface LibraryProps {
  settings: AppSettings;
  updateSetting<K extends keyof AppSettings>(key: K, value: AppSettings[K]): void;
  browse(): Promise<void>;
  search(offset?: number): Promise<void>;
  searchInput: React.RefObject<HTMLInputElement | null>;
  items: CacheEntry[];
  selectedIds: Set<string>;
  setSelectedIds: React.Dispatch<React.SetStateAction<Set<string>>>;
  focusedId: string | null;
  focus(item: CacheEntry): void;
  focusedItem: CacheEntry | null;
  focusedDetails: CacheDetails | null;
  detailsLoading: boolean;
  detailsOffset: number;
  setDetailsOffset(value: number): void;
  selectedSegmentIds: Set<string>;
  setSelectedSegmentIds(value: Set<string>): void;
  cachePage: CachePageState;
  pageTo(offset: number): Promise<void>;
  busy: boolean;
  play(targets?: SelectionTarget[]): Promise<void>;
  exportMedia(): Promise<void>;
  moveToTrash(): void;
  clear(): void;
}

export const LibraryPage = memo(function LibraryPage(props: LibraryProps) {
  const { settings, updateSetting } = props;
  return <div className="library-layout">
    <section className="card root-card">
      <div className="field grow"><label htmlFor="root-path">缓存根目录</label><div className="input-action"><input id="root-path" value={settings.rootPath} readOnly title="请使用右侧按钮选择目录，或在设置页输入后验证切换" placeholder="选择 B 站 download 缓存目录" /><button className="icon-button" aria-label="浏览缓存目录" onClick={() => void props.browse()} disabled={props.busy}><Icon name="folder" /></button></div></div>
      <label className="toggle"><input type="checkbox" checked={settings.includeIncomplete} onChange={(event) => updateSetting('includeIncomplete', event.target.checked)} /><span />包含未完成缓存</label>
    </section>
    <section className="card search-card">
      <div className="search-box"><Icon name="search" /><input ref={props.searchInput} value={settings.keyword} onChange={(event) => updateSetting('keyword', event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !props.busy) void props.search(); }} placeholder="搜索标题、UP 主、BV 号或 AV 号" /></div>
      <select aria-label="匹配方式" value={settings.matchMode} onChange={(event) => updateSetting('matchMode', event.target.value as AppSettings['matchMode'])}><option value="contains">包含</option><option value="prefix">前缀</option><option value="exact">精确</option></select>
      <button className="button secondary" onClick={() => void props.search()} disabled={props.busy}>筛选</button>
      <details className="filter-menu"><summary>高级筛选</summary><div className="filter-popover">
        <Check label="分词" checked={settings.splitKeywords} onChange={(value) => updateSetting('splitKeywords', value)} />
        <Check label="任意关键字" checked={settings.anyKeywords} onChange={(value) => updateSetting('anyKeywords', value)} />
        <Check label="分段名" checked={settings.includePartName} onChange={(value) => updateSetting('includePartName', value)} />
        <Check label="UP 主" checked={settings.includeOwnerName} onChange={(value) => updateSetting('includeOwnerName', value)} />
        <Check label="BV 号" checked={settings.includeBvid} onChange={(value) => updateSetting('includeBvid', value)} />
        <Check label="AV 号" checked={settings.includeAvid} onChange={(value) => updateSetting('includeAvid', value)} />
        <Check label="大小写敏感" checked={settings.caseSensitive} onChange={(value) => updateSetting('caseSensitive', value)} />
      </div></details>
    </section>
    <section className="card cache-panel">
      <div className="panel-heading"><div><h2>缓存列表</h2><span>{props.cachePage.totalItems} 项</span></div><div className="toolbar"><button className="button ghost" onClick={() => void props.play()} disabled={props.busy || (!props.selectedIds.size && !props.selectedSegmentIds.size)}><Icon name="play" />播放</button><button className="button ghost" onClick={() => void props.exportMedia()} disabled={props.busy || (!props.selectedIds.size && !props.selectedSegmentIds.size)}><Icon name="export" />导出</button><button className="button ghost danger-text" onClick={props.moveToTrash} disabled={props.busy || !props.selectedIds.size}><Icon name="delete" />删除</button><button className="button ghost" onClick={props.clear} disabled={props.busy}>清空结果</button></div></div>
      <VirtualizedCacheTable items={props.items} selectedIds={props.selectedIds} setSelectedIds={props.setSelectedIds} focusedId={props.focusedId} focus={props.focus} busy={props.busy} play={props.play} />
      <PageControls
        label="缓存"
        {...props.cachePage}
        busy={props.busy}
        onPage={(offset) => void props.pageTo(offset)}
      />
    </section>
    <section className="card segment-panel"><div className="panel-heading"><div><h2>分段详情</h2><span>{props.focusedDetails ? `${props.focusedDetails.item.title} · ${props.focusedDetails.totalItems} 个分段` : props.focusedItem ? props.focusedItem.title : '选择一条缓存查看'}</span></div></div>
      {props.detailsLoading
        ? <Empty compact icon="film" title="正在加载分段" body="仅为当前页解析媒体结构与可播放状态。" />
        : props.focusedDetails
          ? <><div className="table-scroll"><table><thead><tr><th className="check-cell"><input aria-label="选择全部分段" type="checkbox" checked={props.focusedDetails.segments.length > 0 && props.selectedSegmentIds.size === props.focusedDetails.segments.length} onChange={(event) => props.setSelectedSegmentIds(event.target.checked ? new Set(props.focusedDetails!.segments.map((item) => item.id)) : new Set())} /></th><th>Page</th><th>分段名</th><th>结构</th><th>类型</th><th>大小</th><th>时长</th><th>可播放</th></tr></thead><tbody>{props.focusedDetails.segments.map((segment) => <SegmentRow key={segment.id} item={segment} checked={props.selectedSegmentIds.has(segment.id)} toggle={() => props.setSelectedSegmentIds(toggleSet(props.selectedSegmentIds, segment.id))} play={() => { if (props.busy) return; props.setSelectedSegmentIds(new Set([segment.id])); void props.play([{ avid: props.focusedDetails!.avid, pageIndexes: [segment.pageIndex] }]); }} />)}</tbody></table></div><PageControls label="分段" offset={props.focusedDetails.offset} pageSize={props.focusedDetails.pageSize} totalItems={props.focusedDetails.totalItems} hasMore={props.focusedDetails.hasMore} busy={props.busy} onPage={props.setDetailsOffset} /></>
          : props.focusedItem
            ? <Empty compact icon="film" title="分段详情不可用" body="请重新选择缓存，或重新扫描以刷新索引。" />
            : <Empty compact icon="film" title="没有选择缓存" body="单击上方缓存后按页查看媒体结构；双击分段可直接播放。" />}
    </section>
  </div>;
});

type CacheTableProps = Pick<LibraryProps, 'items' | 'selectedIds' | 'setSelectedIds' | 'focusedId' | 'focus' | 'busy' | 'play'>;
export const VirtualizedCacheTable = memo(function VirtualizedCacheTable(props: CacheTableProps) {
  const toggleRow = useCallback((id: string) => props.setSelectedIds(current => toggleSet(current, id)), [props.setSelectedIds]);
  const playRow = useCallback((item: CacheEntry) => {
    if (props.busy) return;
    props.setSelectedIds(new Set([item.id]));
    void props.play([{ avid: item.avid }]);
  }, [props.busy, props.setSelectedIds, props.play]);
  const rowHeight = 54;
  const overscan = 5;
  const viewport = useRef<HTMLDivElement>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(420);
  useLayoutEffect(() => {
    const element = viewport.current;
    if (!element) return;
    const measure = () => setViewportHeight(element.clientHeight || 420);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  useEffect(() => {
    if (viewport.current) viewport.current.scrollTop = 0;
    setScrollTop(0);
  }, [props.items]);
  const first = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan);
  const visibleCount = Math.ceil(viewportHeight / rowHeight) + overscan * 2;
  const last = Math.min(props.items.length, first + visibleCount);
  const visibleItems = useMemo(() => props.items.slice(first, last), [props.items, first, last]);
  return <div
    ref={viewport}
    className="table-scroll cache-table"
    data-virtualized="true"
    onScroll={(event) => {
      setScrollTop(event.currentTarget.scrollTop);
      setViewportHeight(event.currentTarget.clientHeight || 420);
    }}
  ><table><thead><tr><th className="check-cell"><input aria-label="选择全部缓存" type="checkbox" checked={props.items.length > 0 && props.selectedIds.size === props.items.length} onChange={(event) => props.setSelectedIds(event.target.checked ? new Set(props.items.map((item) => item.id)) : new Set())} /></th><th>视频</th><th>UP 主</th><th>标识</th><th>时长</th><th>分段</th><th>大小</th><th>状态</th><th>更新时间</th></tr></thead><tbody>
    {first > 0 && <tr className="virtual-spacer" aria-hidden="true"><td colSpan={9} style={{ height: first * rowHeight }} /></tr>}
    {visibleItems.map(item => <CacheRow key={item.id} item={item} focused={props.focusedId === item.id} checked={props.selectedIds.has(item.id)} focus={props.focus} play={playRow} toggle={toggleRow} />)}
    {last < props.items.length && <tr className="virtual-spacer" aria-hidden="true"><td colSpan={9} style={{ height: (props.items.length - last) * rowHeight }} /></tr>}
  </tbody></table>
    {!props.items.length && <Empty icon="library" title="尚未加载缓存" body="选择缓存根目录后点击“扫描缓存”，这里会显示可播放与可导出的缓存。" />}
  </div>;
});

export function SegmentRow({ item, checked, toggle, play }: { item: CacheSegment; checked: boolean; toggle(): void; play(): void }) {
  return <tr onDoubleClick={play}><td className="check-cell"><input aria-label={`选择分段 ${item.partName}`} type="checkbox" checked={checked} onChange={toggle} /></td><td>{item.pageIndex}</td><td><strong>{item.partName || item.segmentKey}</strong><small>{item.segmentKey}</small></td><td>{item.structureKind}</td><td>{item.materialKind}</td><td>{formatBytes(item.sizeBytes)}</td><td>{formatDuration(item.durationSeconds)}</td><td><span className={item.isPlayable ? 'dot good' : 'dot'} />{item.isPlayable ? '可播放' : '不可用'}</td></tr>;
}

const CacheRow = memo(function CacheRow({ item, focused, checked, focus, play, toggle }: { item: CacheEntry; focused: boolean; checked: boolean; focus(item: CacheEntry): void; play(item: CacheEntry): void; toggle(id: string): void }) {
  return <tr data-cache-row="true" className={focused ? 'focused cache-row' : 'cache-row'} onClick={() => focus(item)} onDoubleClick={() => { play(item); }}><td className="check-cell" onClick={(event) => event.stopPropagation()}><input aria-label={`选择 ${item.title}`} type="checkbox" checked={checked} onChange={() => toggle(item.id)} /></td><td><strong className="title-cell">{item.title || '未命名缓存'}</strong><small>av{item.avid}</small></td><td>{item.ownerName || '—'}</td><td><code>{item.bvid || '—'}</code></td><td>{formatDuration(item.durationSeconds)}</td><td>{item.segmentCount}</td><td>{formatBytes(item.sizeBytes)}</td><td><span className={item.isAllCompleted ? 'badge success' : 'badge warning'}>{item.isAllCompleted ? '完整' : '未完成'}</span></td><td>{formatDate(item.lastUpdated)}</td></tr>;
});
