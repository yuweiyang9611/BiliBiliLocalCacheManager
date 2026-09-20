# Playback review follow-up (2026-09-20)

This records the Playback-owned findings in the external review. It is not a
release acceptance record and does not claim real desktop player acceptance.

## Implemented findings

| Finding | Resolution |
| --- | --- |
| 1.2: FFmpeg can stall indefinitely | Each transcoding attempt has a 10-minute **no measurable progress** deadline. Increasing probe completion, FFmpeg percentage or processed seconds renews it; repeated/backwards counters, waiting text and phase changes do not. Healthy work has no fixed total duration limit. Timeout cancels only the child FFmpeg/FFprobe processing token; it does not terminate the Host. |
| 1.4: installed FFmpeg redundantly hashes the archive | For the downloaded bundle, a complete extraction keyed by the manifest SHA is checked before accessing the archive. The existing versioned marker plus required-file checks remain. Explicit archive overrides still require the manifest-pinned SHA validation. This preserves the existing extracted-binary trust model; it does not claim new binary-tamper detection. |
| 2.3: unconstrained simultaneous transcodes | One process-wide async gate defaults to 2 operations across all transcoder instances. Slot waits are cancellable and do not manufacture progress. |
| 2.4: redundant cleanup traversal | Reuse the second snapshot when no additional capacity candidates were attempted. A fresh third snapshot remains necessary after additional attempts to report actual outcomes, and per-candidate lock/protection/metadata revalidation remains intact. Empty-directory traversal is retained to reclaim pre-existing empty managed directories too. |
| 2.14: sync-over-async materialization | Added async contracts with compatibility defaults; the built-in materializer, artifact store, semaphore waits, cross-process file-lock polling and FFmpeg processing now await asynchronously. Existing synchronous APIs remain wrappers for CLI and existing clients. |
| 2.15: protection monitor spans blocking I/O | Register and renewal snapshot state under the monitor, then perform protection writes outside it. Renewal callbacks cannot overlap. Stopping cancels outstanding lock waits before joining timer work; existing durable protection is never shortened. |
| 2.16: partial TimeProvider adoption | Retention planning, cleanup and cache-hit timestamp refresh use the store's injected clock. |
| 2.17: fragile downloads and noisy copy progress | Retry transient HTTP/network failures up to 3 attempts with exponential delay; resume partial content with Range, validate the response range and restart if the server ignores Range. The full archive still must match the pinned SHA before publication. Unknown-length streams report real byte progress. Network reads use a renewing 10-minute idle deadline, not a total-time deadline. Copy percentage and forwarded bootstrap byte progress are throttled to 200 ms. Partial files are removed on final failure/cancellation as before. |
| Linux version probe can block before its timeout | Concurrently drain stderr and stdout with a bounded cancellation deadline. A stalled probe is killed as its own process tree; no Host process is affected. |
| Duplicated DASH planning | NewDash and MidDash reuse one internal plan builder; layout detection and priority remain unchanged. |
| Legacy no-op ternary / duplicate artifact directory creation | Removed without changing plan or publication behavior. |
| Missing launcher and concat tests | Added injected process-start tests (no real desktop launch), special-path argument checks, missing-player/fallback checks, and a real FFmpeg concat packet-preservation test. |

## Configuration and intentional boundaries

- `BILIBILI_LOCAL_CACHE_MANAGER_MAX_TRANSCODES`: optional process-start environment
  variable, integer 1 through 16, default 2. Invalid or out-of-range values use 2.
  It is process-global and is read once; changing it requires restarting the app.
- Bootstrap retains its thread-affine named cross-process `Mutex`. Awaiting while
  holding that mutex could resume on a different thread and break ownership.
  Bootstrap setup therefore runs on a worker; network and file operations inside
  it remain compatibility sync wrappers. There is no claim that every bootstrap
  wait became thread-free. The long-running media processing path did.
- The existing small best-effort `TryDeleteFile` helpers are not consolidated into
  a new cross-layer abstraction. Their failure handling and ownership differ;
  removing them is not a reliability fix.
- Protection timer writes are not connected to preparation progress callbacks.
  Renewing a six-hour protection marker cannot conceal stalled transcoding.
- Real Windows/Linux file associations, mpv and VLC interactions still require
  a maintainer's desktop acceptance test. Unit tests do not mark those passed.
- Idle expiry is tested with an injected clock, and real FFmpeg integration
  exercises the cancellation path. A deliberately hung real FFmpeg/FFprobe
  subprocess terminated by the idle watchdog has not been exercised end to end.
- Range retries do not currently send ETag/If-Range. A changed remote asset can
  therefore fail the final pinned SHA check rather than resume successfully;
  mismatched bytes are never published.

## Verification

- Playback regression suite: **119 passed**, 0 failed, 0 skipped.
- Pinned real FFmpeg integration suite: **6 passed**, 0 failed, 0 skipped.
- New deterministic tests cover progress beyond ten minutes, rollback/phase
  cycling, late timer callbacks, gate cancellation, unknown-length downloads
  exceeding ten minutes, stalled reads, interrupted range resume/server fallback,
  transient versus permanent HTTP errors, clock-based retention/touch, blocked
  protection renewal, silent version-probe timeout and stderr draining.
- Follow-up review caught underestimated media durations reaching 100 percent
  before FFmpeg finishes. Concat, mux and AAC fallback now always use the actual
  processed-time callback, forwarding unbounded processed seconds while clamping
  only the displayed percentage. Virtual-clock tests cover two hours beyond the
  duration estimate followed by a genuine stall; the real mux test deliberately
  supplies a 50 ms estimate for a two-second fixture.
- Existing protection tests still cover more than 64 artifacts, more than six
  hours of preparation, independent cleanup instances, concurrent requests,
  protection failure, cancellation and eventual expiration.
- No Release, desktop acceptance record, commit, push or merge was created.

Commands used:

```powershell
dotnet test BiliBiliLocalCacheManager.Playback.Tests/BiliBiliLocalCacheManager.Playback.Tests.csproj --configuration Release --no-restore --nologo --filter "Category!=FFmpegIntegration"
$env:BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH = & ./scripts/prepare-ffmpeg-integration.ps1
$env:BILIBILI_RUN_FFMPEG_INTEGRATION_TESTS = '1'
dotnet test BiliBiliLocalCacheManager.Playback.Tests/BiliBiliLocalCacheManager.Playback.Tests.csproj --configuration Release --no-build --no-restore --nologo --filter 'Category=FFmpegIntegration'
```
