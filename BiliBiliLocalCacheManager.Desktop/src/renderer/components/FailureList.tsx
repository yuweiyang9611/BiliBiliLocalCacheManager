import { memo, useEffect, useState } from 'react';
import type { MediaFailure } from '../../shared/contracts';
import { formatFailureTarget } from '../display';
import { PageControls } from './Common';

export const FailureList = memo(function FailureList({ failures }: { failures: MediaFailure[] }) {
  const [offset, setOffset] = useState(0);
  const pageSize = 100;
  useEffect(() => setOffset(0), [failures]);
  return <details open><summary>失败明细</summary><ul className="result-list">
    {failures.slice(offset, offset + pageSize).map((failure, index) => <li key={offset + index}><div>
      <strong>{formatFailureTarget(failure)}</strong>
      <p>{failure.message}</p>
    </div></li>)}
  </ul><PageControls label="失败明细" offset={offset} pageSize={pageSize} totalItems={failures.length}
    hasMore={offset + pageSize < failures.length} busy={false} onPage={setOffset} /></details>;
});
