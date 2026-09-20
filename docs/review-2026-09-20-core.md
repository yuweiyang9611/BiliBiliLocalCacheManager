# Core and CLI review follow-up (2026-09-20)

This records the Core/CLI portion of the external review against main commit
`61e532b3092acb1e1923999bbf4b6a1c84afbb6d`. Recommendations were checked against
the code; they were not treated as authority to remove safety checks or change
established cancellation/snapshot semantics. Other components have separate
implementation and verification records.

## Numbered findings

| Finding | Disposition and evidence |
| --- | --- |
| 1.1 Direct deletion safety | Fixed. `FileSystemCacheDeletionService` delegates to a shared partial of `FileSystemCacheTrashService`. Direct deletion now participates in the same root semaphore and named mutex as move/restore/purge; it locks the root and every recursive directory, validates the target's physical file identity, and deletes files/directories through non-reparse handles. Unsupported platforms fail closed for actual permanent deletion, matching the existing purge policy; dry-run remains available. |
| 1.10 Search snapshots/cancellation | Partly inaccurate. `CacheIndex.Search(options, CancellationToken)` and cancellable filesystem scans already existed, and desktop searches use the current index snapshot rather than `CacheManager.Search(root, ...)`. Added missing cancellable `ICacheManager`/`CacheManager` Search and FindByAvid overloads, including bool convenience overloads. Legacy builder fallbacks check cancellation before and after their synchronous call. No hidden global root cache was introduced: callers that need snapshot reuse already hold `CacheIndex`, while one-shot CLI commands require fresh disk state. |
| 2.1 Duplicate tree statistics | Fixed by shared iterative `DirectoryTreeInspector`. File metadata is refreshed once then its attributes and length are reused; directories are revalidated immediately before descent. The report's proposed zero allocation/zero additional stat claim is not valid for `FileSystemInfo` objects and is not claimed here. Explicit reparse checks and cancellation remain. Statistics are still a best-effort read-only snapshot, not authorization to delete. |
| 2.2 Repeated listing state reads | Simplified the read-only path to pass the already parsed entry identity/marker to metadata validation. Removed the repeated absent-marker probe and deserialize directly from the already parsed JsonDocument. The original report overstates the marked-entry case: the previous short-circuit skipped EnsureTrashIdentity when a marker/journal existed, so an existing marker was not actually parsed twice in that branch. Mutation-time identity checks were retained. |
| 2.12 CLI options/AV IDs | Fixed. show/play now use the same positive-number/optional-av-prefix parser as delete/restore. All six command option sets use common root/help/include-incomplete definitions. A shared command catalog drives dispatch, command suggestions and topic help, including trash. Command-specific error/usage handling remains local. |

## Low-priority findings

| Finding | Disposition |
| --- | --- |
| Purge method size, repeated failure accounting | Remaining structural suggestion. The journal recovery algorithm and its repeated validation barriers were not rewritten merely for line-count reduction. Existing partial-deletion, interrupted-journal and external-replacement regression tests continue to pass. |
| Purge deletes measure bytes that are not directly summed | Retained deliberately. Reported freed bytes use the initial user-data snapshot and reconciliation with remaining bytes on failure. Blindly summing deletions would count marker/journal files created by the purge itself as additional reclaimed user data. |
| Duplicate atomic state writers | Fixed with `WriteStateFileAtomically`; metadata serialization and raw recovery contents share CreateNew, WriteThrough, durable flush, non-overwriting rename and cleanup. Post-write readback checks remain at their transaction-specific boundaries. |
| Scanner repeated AV parsing / File.Exists | Fixed. AV path and parsed ID travel together; entry.json is read directly and a missing file is a normal skip. Directory checks at enumeration and use are deliberately both retained because a directory can change between those points. |
| BiliVideoCache aggregate recomputation | Fixed. Immutable segment aggregates are calculated once in the constructor with the same negative-value handling and saturation semantics. |
| Search per-item allocations | Fixed the per-cache/token LINQ delegate chain with explicit loops. AV text is invariant-culture and lazily formatted at most once per cache for the query. Cancellation, AND/OR semantics, field matching and source ordering are covered by regressions. No machine-dependent performance threshold was added. |
| JSON library migration | Not performed. The report provides no demonstrated defect or compatibility corpus justifying removal of Newtonsoft's established legacy-input coercions. A System.Text.Json migration would need differential legacy fixtures and measurements, not just a dependency substitution. |
| MutationGates permanent retention | Fixed using reference-counted gate entries including waiters, release on failed acquisition and disposal/removal after the final reference. Different roots remain independent; the cross-process mutex name is unchanged. |
| Default scan-report implementation hides unavailable diagnostics | Fixed with `CacheIndexBuildResult.IssuesCollected`. Real reports default true; FromIndex legacy fallback explicitly returns false. Existing call signatures remain compatible. |
| catch-all disk result wrappers | Move, restore and direct delete now catch the existing expected filesystem/safety exception whitelist. Programming exceptions propagate instead of being mislabeled as ordinary disk failures. Cleanup-only best-effort catches still preserve an already-raised primary failure. |
| CLI help / duplicate names / constructor cleanup | Fixed. The catalog includes trash topic help and replaces duplicated dispatch/name lists. Playback service creation and cache maintenance are lazy, so construction, help and rejected arguments no longer run cleanup. Actual playback still initializes the service before preparation. |
| Core deletion/search coverage | Expanded. Added handle-lock and shared-transaction tests, preflight link rejection before payload deletion, unsupported-platform fail-closed behavior, programming-error propagation, shared-inspector cancellation/replacement tests, search modes/AND/OR/order/case/cancellation, and AV-prefix/invalid-value CLI tests. |

## Safety choices

- Direct deletion retains a preflight inspection while its target handle is held.
  It is followed by handle-based mutation checks. Combining these into a single
  destructive traversal would regress the existing behavior that an already
  linked tree is rejected before any payload is removed.
- The statistics inspector refreshes entries after its activity callback and
  validates each directory again before traversal. Cached metadata is not a
  physical-identity lease and is never reused to authorize a mutation.
- Existing v3 wire responses, trash metadata formats, purge journals, mutex
  names, atomic export rules and playback protections are unchanged by this
  Core/CLI work.
- Linux permanent-delete refusal is exercised by a platform-specific test but
  was not run on Linux in this Windows session. Link tests retain the existing
  suite convention of returning when the OS denies link creation.

## Verification

Executed from the repository root on Windows with .NET 10:

```powershell
dotnet test BiliBiliLocalCacheManager.Core.Tests/BiliBiliLocalCacheManager.Core.Tests.csproj --configuration Release --no-restore --nologo
dotnet test BiliBiliLocalCacheManager.Cli.Tests/BiliBiliLocalCacheManager.Cli.Tests.csproj --configuration Release --no-restore --nologo
git diff --check -- BiliBiliLocalCacheManager.Core BiliBiliLocalCacheManager.Core.Tests BiliBiliLocalCacheManager.Cli BiliBiliLocalCacheManager.Cli.Tests
```

Results: Core **163 passed** (previously 142); CLI **53 passed** (previously 43).
The whitespace check passed; Git only reported its configured LF-to-CRLF
normalization warnings. No commit, push, release or external workspace change
was performed by this scoped implementation.
