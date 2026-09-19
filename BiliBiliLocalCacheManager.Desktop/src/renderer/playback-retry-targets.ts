import type { MediaFailure, SelectionTarget } from '../shared/contracts';

const maximumTargets = 1_000;
const maximumPagesPerTarget = 10_000;
const maximumTotalPages = 20_000;

export function buildPlaybackRetryTargets(failures: readonly MediaFailure[]): SelectionTarget[] {
  const grouped = new Map<string, Set<number> | null>();
  for (const failure of failures) {
    if (failure.pageIndex === null) {
      grouped.set(failure.avid, null);
    } else if (!grouped.has(failure.avid)) {
      grouped.set(failure.avid, new Set([failure.pageIndex]));
    } else {
      grouped.get(failure.avid)?.add(failure.pageIndex);
    }
  }

  const targets: SelectionTarget[] = [];
  let totalPages = 0;
  for (const [avid, pages] of grouped) {
    if (pages === null) {
      targets.push({ avid });
    } else {
      totalPages += pages.size;
      if (totalPages > maximumTotalPages) {
        throw new RangeError('失败分段超过单次重试的 20000 个分段限制，请分批选择失败分段后播放。');
      }
      const pageIndexes = [...pages];
      for (let offset = 0; offset < pageIndexes.length; offset += maximumPagesPerTarget) {
        targets.push({ avid, pageIndexes: pageIndexes.slice(offset, offset + maximumPagesPerTarget) });
      }
    }
    if (targets.length > maximumTargets) {
      throw new RangeError('失败项目超过单次重试的 1000 个目标限制，请分批选择失败项目后播放。');
    }
  }
  return targets;
}
