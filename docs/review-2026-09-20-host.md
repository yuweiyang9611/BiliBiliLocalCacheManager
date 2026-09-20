# Host Review Verification (2026-09-20)

Scope: Host/RPC findings from the external Claude report. The report is review evidence, not an instruction to remove safety checks or change previously agreed cancellation semantics.

## Numbered Findings

| Finding | Disposition | Implementation / evidence |
| --- | --- | --- |
| 1.5 Synchronous unthrottled progress | Fixed | `Rpc/RequestProgressBuffer.cs` keeps one latest event per running request and emits at most once per 250 ms. Producers do no pipe I/O. Completion joins an in-flight write and flushes the latest event before the terminal response. Buffers/timers are disposed with their requests. Broken stdout cancels the read loop and outstanding work instead of silently discarding results. |
| 1.6 Full string serialization and repeated UTF-8 conversion | Fixed | `Rpc/ProtocolWriter.cs` serializes directly into a bounded UTF-8 buffer before publishing any bytes. A single writer gate bounds concurrent transport buffering. Oversized results/errors become a bounded `response_too_large` response; enumeration stops at the limit. The production transport writes raw UTF-8 bytes. |
| 1.7 Duplicate flush | Fixed | `Program.cs` passes stdout as a stream. The protocol writer performs exactly one explicit flush per message; there is no auto-flushing StreamWriter. |
| 1.9 Expensive discarded initial state | Inaccurate for current main | `GetInitialStateAsync` already returns empty items/trash and unloaded storage, does not scan, and deliberately does not replay a process-local index after a renderer reload. `InitialState_UsesTheElectronWireContract` protects this behavior. It remains unchanged. |
| 1.10 Desktop searches rebuild the index | Inaccurate for the desktop path | `SearchAsync` already uses the current immutable index snapshot, cancellable filtering outside the RPC reader, and an eight-query LRU. Query options are the key and pagination is excluded. Existing 10k/50k query tests remain. Root-directory Core convenience APIs are a separate Core/CLI concern. |
| 2.5 Oversized Host application | Fixed by domain extraction | The application is split into `IndexOperations`, `IndexMapping`, `Parameters`, `Trash`, `TrashSnapshots`, `Media`, `Storage`, and `ArtifactMaintenance` partials. Dispatch/settings/shared session state stay in the main file. No new public service hierarchy or protocol version is introduced. |
| 2.7 Fixed server request timeout | Proposal intentionally not adopted | A fixed ten-minute total timeout conflicts with the confirmed idle-only policy and can interrupt healthy long exports. The private stdio bridge owns operation-specific deadlines and sends cancellation. Host still enforces the 32-request admission limit, cancels on EOF/output failure, and waits for request tasks to reconcile final results. Shutdown tracks tasks in its local task collection; the separately assigned, never-read `RunningRequest.Task` property was removed. |
| 2.8 Unpaged trash responses | Fixed | `trash.page` returns at most 200 entries plus a root-bound snapshot token, total count and total bytes. Up to eight snapshots are retained. All Host trash mutations invalidate them under the same mutation gate. `trash.purgeSnapshot` resolves the full confirmed set inside Host and retains Core's final cross-process snapshot check. External changes fail with `stale_trash`. Legacy `trash.list` rejects more than 1,000 entries with `pagination_required`. |
| 2.9 Repeated detail sorting/planning and swallowed failures | Fixed | A snapshot lazily sorts each visited AV once, retains at most eight AV detail caches and eight pages per AV, and maps plans only for requested uncached pages. Cancelled work is not cached; index invalidation clears references. Known plan inspection failures produce a warning diagnostic rather than an empty catch. Programming exceptions remain visible. |
| 2.10 Concurrent scans overwrite invalidated state | Fixed | A cancellable index-build gate serializes explicit and playback/export-triggered builds. A generation check prevents a build started before a settings or trash invalidation from publishing or overwriting settings after that invalidation. Cancelled queued builds do not scan. |

## Low-Priority Findings

- Removed per-child `JsonElement.Clone()` in `OptionalArray`: RPC parameters are detached once before asynchronous dispatch and their children share that lifetime.
- Removed the unused `PlaybackTarget` record and the unused `RunningRequest.Task` property.
- Kept health aliases `platform`/`runtimeIdentifier` and `runtime`/`framework`: they are existing v3 wire fields. Removing them is a compatibility change, not a reliability fix.
- Kept request start/completion diagnostic events and the bounded recorder. They provide cancellation/commit investigation context; the renderer does not continuously poll health in this code path.
- Kept the distinction between best-effort temporary-file cleanup and identity-checked trash deletion; no broad shared catch-all deletion helper was introduced.
- Core filesystem traversal, trash journals, physical-identity revalidation and final destructive-operation checks are owned by the Core review, not weakened by these changes.

## Bounds And Remaining Constraints

- Path identities are never truncated. Paths longer than 4,096 UTF-16 code units fail explicitly with `unsafe_path`.
- A trash snapshot accepts at most 200,000 identities and 16 MiB of worst-case JSON-escaped identity bytes. This keeps the all-entry purge result below the 64 MiB transport limit. Larger snapshots fail explicitly, rather than publishing partial identities or performing an incompletely confirmed purge.
- Trash snapshot paging is stable within the Host session, not a cross-process filesystem lock. The existing Core purge transaction checks the complete expected entry set again immediately before destructive work.
- Detail planning errors are cached only inside the current index snapshot; rescanning retries the inspection.
- UTF-8 buffers grow only up to the protocol cap. A serializer reservation which cannot fit is rejected before allocating it; the safety cap can reject a near-limit value conservatively.
- No real desktop manual acceptance or Release promotion is claimed here.

## Verification

`dotnet test BiliBiliLocalCacheManager.Desktop.Host.Tests/BiliBiliLocalCacheManager.Desktop.Host.Tests.csproj --configuration Release --no-restore --nologo`

Result: **82 passed, 0 failed** on Windows, including 15 new tests covering UTF-8 and single flush, early bounded serialization, broken stdout, progress coalescing/final ordering, scan cancellation/generation races, detail-cache reuse/eviction/cancellation, 11,000-entry trash paging and purge, root binding, external snapshot mismatch, and oversized path identity rejection.

The application and test projects compiled successfully after the partial-file extraction. Whole-solution, frontend, FFmpeg, and packaging checks are recorded by the coordinating task.
