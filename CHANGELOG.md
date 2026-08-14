# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Added

- Export cached videos to ordinary MP4 files named after the video title, for a single page or a whole multi-selection, reusing the transcode cache so already-generated artifacts export instantly.
- Add a `trash` command group to the CLI (`list`, `restore`, `stats`, `purge`) backed by a new trash-entry enumeration API in Core.
- Enable the WPF Fluent theme with automatic light/dark switching that follows the Windows app-mode setting.
- Ship an application icon and show the product version in the window title.
- Guide first-time users with empty-state messages that distinguish "not scanned yet", "directory contains no cache", and "no search matches".
- Scan automatically on startup when a cache directory is remembered, and right after one is picked.
- Filter the list as you type, submit with Enter, and treat an empty keyword as "show everything".
- Add keyboard shortcuts (F5 scan, Ctrl+F search, Delete, Ctrl+Z undo, Ctrl+E export, Esc cancel) and double-click-to-play on both grids.
- Show UP owner, BV id, and duration columns in the cache list.
- Warm up FFmpeg in the background at startup so the first playback no longer waits for the bundle download.
- Catch unhandled exceptions and write redacted crash reports to LocalAppData instead of disappearing silently.
- Suggest the intended command when an unknown command or `trash` sub-command is typed.
- Persist generated playback artifacts under LocalAppData and reuse them across application restarts.
- Add WPF transcode-cache statistics plus open, cleanup, and confirmed clear actions.
- Serialize same-artifact generation across processes and make lock waiting cancellable.
- Report progress for first-run FFmpeg download, verification, and extraction, with cancellation and safe retry support.
- Add WPF settings for transcode-cache retention days and capacity limits.
- Add confirmed permanent cleanup for application-managed trash entries.
- Add versioned v1 trash identity metadata and separate capacity statistics for metadata-verified entries and legacy entries without metadata.
- Add a second explicit confirmation before adopting and permanently deleting legacy name-only trash entries.
- Add a unified WPF storage overview for original cache, transcode artifacts, application trash, and reclaimable space.
- Add non-blocking startup and post-playback transcode-cache maintenance with session protection for launched artifacts.
- Add versioned settings migration, corrupt-file backup, and future-schema overwrite protection across processes.
- Add privacy-redacted diagnostic ZIP export with runtime, storage, settings, FFmpeg, and recent-event snapshots.
- Add opt-in real FFmpeg integration tests for stream copy, AAC fallback, cancellation, cache reuse, and source invalidation.

### Changed

- **CLI `delete` now moves the cache to the application trash by default instead of deleting it permanently.** Permanent deletion requires the new `--permanent` flag, both paths ask for confirmation unless `--yes` is passed, and a non-interactive shell without `--yes` refuses to act. This closes the gap where the GUI deleted safely but one CLI command destroyed data with no confirmation.
- Accept `av`-prefixed ids (for example `av170001`) wherever the CLI takes an avid.
- Size buttons to their content so Chinese labels are never clipped when font metrics change.
- Copy both DASH streams when audio is AAC; for other codecs, copy video and convert only audio to AAC for player compatibility.
- Skip redundant video-duration probing when reliable duration metadata is already available.
- Increase default playback-artifact retention to 30 days and the size limit to 20 GB.
- Limit configurable playback-artifact retention to five years and capacity to 128 GB.
- Keep valid metadata-backed legacy trash entries manageable after the cache root is relocated; missing-metadata legacy entries remain excluded from normal cleanup.
- Keep v1 trash entries untouched in older builds that do not understand their identity format; use a v1-aware build to restore or purge them.
- Run startup cleanup only after the main window has rendered, and refresh storage statistics outside foreground scan/delete operations.
- Use one checked-in FFmpeg manifest for runtime, CI, and Release, pinning the exact BtbN tag, asset, URL, and SHA-256 instead of a mutable latest download.
- Prefer the verified FFmpeg bundle by default; system PATH discovery now requires explicit opt-in.

### Fixed

- Keep entries that survived a trash purge undoable; previously purging the trash cleared the entire undo list even for entries that were skipped or failed before deletion started.
- Reject cache entries whose JSON avid does not match their parent cache directory before any delete operation can target them.
- Avoid cache misses caused only by title or other display-metadata changes.
- Discard generated artifacts when their source media changes during FFmpeg processing.
- Prevent cleanup from deleting unmanaged files, reparse-point targets, or another process's active build artifact.
- Tolerate artifacts disappearing while another application instance is concurrently inspecting or clearing the cache.
- Reject incomplete FFmpeg downloads and extractions instead of reusing partial installations.
- Treat missing required metadata and out-of-range timestamps as invalid entries without aborting the full scan.
- Keep WPF delete and undo state on the UI thread and bind undo batches to their original cache root.
- Prevent the WPF playback queue from advancing more than one item concurrently.
- Clear stale WPF results when the incomplete-cache filter changes.
- Reject zero-byte playback streams and serialize FFmpeg bootstrap across application processes.
- Validate persisted enum settings and return failure exit codes when CLI targets do not exist.
- Release per-artifact synchronization objects after playback materialization completes.
- Reject release versions that do not match the repository version metadata.
- Revalidate cleanup candidates after acquiring their artifact lock so a concurrently reused file is not deleted.
- Include managed build artifacts consistently in statistics, cleanup previews, cleanup results, and confirmed clear operations while preserving active builds.
- Make storage refresh requests supersede stale scans and tolerate concurrent build-to-final promotion.
- Prevent optional FFmpeg version metadata failures from breaking an otherwise successful initialization.
- Show cancellable preparation progress while waiting for another process to generate the same playback artifact, without showing it for an uncontended cache hit.
- Serialize trash move, restore, statistics, and purge operations per normalized cache root across application processes.
- Make trash moves, restores, and permanent deletion handle-bound on Windows, with atomic metadata precommit and repeated identity validation.
- Keep a root-level purge journal bound to the physical directory volume/file ID until entry deletion commits, so missing internal state, process interruption, same-name replacement, and late journal cleanup all fail closed or retry safely.
- Report permanently freed trash capacity as the actual net byte reduction, excluding temporary marker, metadata-recovery, and journal overhead.
- Serialize settings writes across processes, use unique temporary files, and re-check the target before replacing it.
- Bound post-playback artifact protection by count, age, and configured capacity so a long session cannot indefinitely defeat cleanup.
- Stop stale application instances from running automatic maintenance or overwriting same-schema settings after another instance changes them.
- Report incomplete transcode-cache clearing when locked artifacts remain instead of showing a false success.
- Reject reparse-point cache, trash, and media paths before scanning, moving, restoring, purging, or probing them.
- Bound diagnostic history and sensitive-value collection while preserving redaction for played media after large scans.
- Keep first-run FFmpeg preparation compatible with stock Windows PowerShell 5.1 by using its basic web-request parser.
- Keep CI and Release on the SDK feature band required by `global.json` instead of floating to an incompatible future band.
- Pin FFmpeg to a month-end build covered by the provider's long-retention policy instead of an expiring daily build.
- Refresh repeated media names in the diagnostic redaction LRU so recent playback events cannot outlive their sensitive-value protection.
- Skip entries with negative sizes or out-of-range durations without aborting the scan, and saturate aggregate size/duration totals.
- Saturate WPF list and selection byte summaries across multiple extreme cache entries.
- Smoke-test the published self-contained CLI and WPF executables and verify both ZIP payloads before completing a release build.
- Tolerate managed artifact directories disappearing or becoming briefly inaccessible during concurrent statistics, cleanup, and clear operations.
- Reject a cache root that is itself a symbolic link or directory junction before scanning, measuring, trashing, purging, or permanently deleting data.
- Refuse permanent deletion when an avid cache tree contains a symbolic link or directory junction.

## [0.3.0] - 2026-07-11

### Added

- Observable cache scan reports with invalid-entry and inaccessible-directory statistics.
- Cancellation and progress feedback for WPF scanning and search-index creation.
- Page-level playback queue with explicit next-item and clear-queue actions.
- Managed playback artifacts with deterministic reuse, atomic generation, and bounded cleanup.
- WPF application trash with batch safe deletion and undo support.
- Persistent cache path, search, and player preferences.
- Storage-size and last-updated summaries in the WPF cache list.
- Independent playback regression test project and Windows CI workflow.
- Self-contained Windows release packaging with SHA256 checksums.

### Changed

- FFmpeg initialization is delayed until media processing is actually required.
- WPF batch playback no longer launches all selected pages at once.
- WPF deletion moves caches to the application trash; CLI deletion remains permanent.

[Unreleased]: https://github.com/yuweiyang9611/BiliBiliLocalCacheManager/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/yuweiyang9611/BiliBiliLocalCacheManager/releases/tag/v0.3.0
