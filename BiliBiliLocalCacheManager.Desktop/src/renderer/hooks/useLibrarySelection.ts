import { useCallback, useMemo, useState, type SetStateAction } from 'react';
import type { CacheDetails, CacheEntry, CacheSegment, SelectionTarget } from '../../shared/contracts';

type PartChoice = { video: CacheEntry; segment: CacheSegment };
type Selection = { videos: Map<string, CacheEntry>; parts: Map<string, PartChoice> };
const empty = (): Selection => ({ videos: new Map(), parts: new Map() });
export const partSelectionKey = (avid: string, pageIndex: number) => `${avid}:${pageIndex}`;

function compareVideos(left: CacheEntry, right: CacheEntry) {
  const date = (Date.parse(right.lastUpdated ?? '') || 0) - (Date.parse(left.lastUpdated ?? '') || 0);
  return date || (BigInt(left.avid) < BigInt(right.avid) ? -1 : BigInt(left.avid) > BigInt(right.avid) ? 1 : 0);
}

export function useLibrarySelection(items: CacheEntry[], details: CacheDetails | null) {
  const [selection, setSelection] = useState<Selection>(empty);
  const clearSelection = useCallback(() => setSelection(empty()), []);
  const setSelectedIds = useCallback((action: SetStateAction<Set<string>>) => setSelection(current => {
    const ids = typeof action === 'function' ? action(new Set(current.videos.keys())) : action;
    const available = new Map([...current.videos, ...items.map(item => [item.id, item] as const)]);
    return { videos: new Map([...ids].flatMap(id => available.has(id) ? [[id, available.get(id)!] as const] : [])), parts: new Map() };
  }), [items]);
  const setSelectedSegmentIds = useCallback((ids: Set<string>) => setSelection(current => {
    const available = new Map(current.parts);
    if (details) for (const segment of details.segments) {
      available.set(partSelectionKey(details.avid, segment.pageIndex), { video: details.item, segment });
    }
    return { videos: new Map(), parts: new Map([...ids].flatMap(id => available.has(id) ? [[id, available.get(id)!] as const] : [])) };
  }), [details]);
  return useMemo(() => {
    const videos = [...selection.videos.values()].sort(compareVideos);
    const parts = [...selection.parts.values()].sort((a, b) => compareVideos(a.video, b.video) || a.segment.pageIndex - b.segment.pageIndex);
    const grouped = new Map<string, SelectionTarget>();
    for (const part of parts) {
      if (!grouped.has(part.video.avid)) grouped.set(part.video.avid, { avid: part.video.avid, pageIndexes: [] });
      grouped.get(part.video.avid)!.pageIndexes!.push(part.segment.pageIndex);
    }
    const targets: SelectionTarget[] = parts.length ? [...grouped.values()] : videos.map(video => ({ avid: video.avid }));
    const count = parts.length || videos.reduce((sum, video) => sum + (video.pageCount ?? video.segmentCount), 0);
    return {
      selectedIds: new Set(selection.videos.keys()), selectedSegmentIds: new Set(selection.parts.keys()),
      setSelectedIds, setSelectedSegmentIds, clearSelection, targets, videos, parts,
      videoCount: targets.length, partCount: count,
      bytes: parts.length ? parts.reduce((sum, part) => sum + part.segment.sizeBytes, 0) : videos.reduce((sum, video) => sum + video.sizeBytes, 0),
      label: parts.length ? `已选 ${targets.length} 个视频中的 ${parts.length} 个分 P` : `已选 ${videos.length} 个视频，索引中 ${count} 个分 P`,
    };
  }, [selection, setSelectedIds, setSelectedSegmentIds, clearSelection]);
}
