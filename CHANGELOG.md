# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Added

- Add independent desktop settings for remembering the cache root and scanning it on startup; startup scanning is opt-in, and schema-v1 users with a saved root must explicitly choose whether to forget it, remember it without scanning, or enable startup scans.
- Add Desktop Host protocol v2 with opaque index tokens, bounded cache-summary pages, and lazily paged segment details; the renderer virtualizes cache rows and never materializes every segment during a scan.
- Add a stable `ci-required` summary job that fails unless privacy checks, the full build/test/package matrix, and Debian/Fedora installed-package smoke tests all succeed.

### Changed

- Keep startup lightweight by loading storage statistics and application-trash entries only when their pages are opened, instead of traversing those directories while the window connects.
- Validate a newly selected cache root with a non-persisting scan before committing the settings change, and retain that validated index instead of traversing the directory again; disabling root persistence now forgets it on the next launch while retaining the validated root for the current session.
- Publish multi-item media exports only after every item succeeds by staging the directory beside its destination and atomically moving it into place.

### Fixed

- Bind every Electron trash mutation to an explicit cache root, and require permanent cleanup to carry the complete non-empty entry snapshot shown by the UI; Core now validates that snapshot after acquiring the per-root cross-process mutation lock and deletes nothing when it is stale, cross-root, or incomplete.
- Prevent a persisted search keyword or an unbound process-local index from bypassing the startup-scan choice or exposing another root's cache rows after a renderer reload.
- Strip development, test, Host-path, FFmpeg, and .NET runtime injection variables before launching a packaged Desktop Host, while preserving only explicitly trusted smoke-test data paths.
- Require a locally overridden Windows FFmpeg archive to match the SHA-256 pinned in the bundled manifest before it can be extracted or executed.
- Bind CLI `trash purge` confirmation to the complete pre-confirmation trash snapshot, include legacy-entry capacity in `--include-untrusted` prompts, and fail with zero deletions when the snapshot changes.
- Make desktop search strictly latest-write-wins, keep domain and cancellation errors distinct from Host transport failure, and propagate cancellation for timeouts, renderer destruction, and every long-running Host IPC operation.
- Reject malformed or mismatched Host v2 initialization data, expose an explicit renderer bootstrap failure state, and make source/packaged smoke tests prove settings loading, startup scanning of a real fixture, and Host IPC end to end.
- Make Electron smoke tests fail closed on premature window shutdown, and exercise installed Debian/Fedora packages as an unprivileged user with a verified Chromium SUID or user-namespace sandbox.

## [0.4.0-rc.1] - 2026-08-27

### Added

- Add the MIT License and include it in CI and release artifacts.
- Export cached videos to ordinary MP4 files named after the video title, for a single page or a whole multi-selection, reusing the transcode cache so already-generated artifacts export instantly.
- Add a `trash` command group to the CLI (`list`, `restore`, `stats`, `purge`) backed by a new trash-entry enumeration API in Core.
- Add an Electron 44 desktop client with its embedded Chromium runtime, a React/TypeScript renderer, and a self-contained .NET 10 JSON-lines host.
- Harden packaged Electron binaries with a traversal-safe custom renderer protocol and fuses that disable file-protocol privileges, run-as-Node, `NODE_OPTIONS`, and main-process inspector arguments, require ASAR loading, and enable ASAR integrity where supported.
- Ship an application icon and show the product version on the Diagnostics page.
- Add empty-state guidance when the cache list has no rows.
- Scan automatically on startup when a cache directory is remembered, and right after one is picked.
- Filter the list as you type, submit with Enter, and treat an empty keyword as "show everything".
- Add keyboard shortcuts (F5 scan, Ctrl+F search, Delete, Ctrl+Z undo, Ctrl+E export, Esc cancel) and double-click-to-play on both desktop data tables.
- Show UP owner, BV id, and duration columns in the cache list.
- Warm up FFmpeg in the background at startup to reduce first-playback preparation latency.
- Suggest the intended command when an unknown command or `trash` sub-command is typed.
- Persist generated playback artifacts under LocalAppData and reuse them across application restarts.
- Add desktop transcode-cache statistics plus open, cleanup, and confirmed clear actions.
- Serialize same-artifact generation across processes and make lock waiting cancellable.
- Report progress for first-run FFmpeg download, verification, and extraction, with cancellation and safe retry support.
- Add desktop settings for transcode-cache retention days and capacity limits.
- Add confirmed permanent cleanup for application-managed trash entries on Windows.
- Add versioned v1 trash identity metadata and separate capacity statistics for metadata-verified entries and legacy entries without metadata.
- Add a second explicit confirmation before adopting and permanently deleting legacy name-only trash entries on Windows.
- Add a unified desktop storage overview for original cache, transcode artifacts, application trash, and reclaimable space.
- Add non-blocking startup and post-playback transcode-cache maintenance with session protection for launched artifacts.
- Add versioned settings loading and migration, corrupt-file backup, and read-only handling when a newer schema is already present at load time.
- Add diagnostic ZIP export with runtime, settings, FFmpeg, transcode-cache, session, and bounded recent-event snapshots; redact known roots, user-home paths, URLs, and named credentials in event messages.
- Add opt-in real FFmpeg integration tests for stream copy, AAC fallback, cancellation, cache reuse, and source invalidation.
- Add a manual release compatibility checklist for real Windows and GNOME/KDE XWayland sessions, including mandatory Fedora 43 coverage on at least one supported desktop.
- Add optional CI Authenticode signing inputs for the Electron executable, embedded .NET Host, and NSIS installer, and fail the Windows build when configured signing cannot complete.
- Extract and smoke the final Windows zip, then silently install, smoke, and uninstall the final NSIS package before CI or Release accepts it.

### Changed

- Replace the previous desktop front end with Electron 44 and its embedded Chromium runtime, keeping cache and media operations in a separately spawned, self-contained .NET 10 host.
- Publish native `win-x64` and `linux-x64` desktop packages: an NSIS installer for Windows and deb/rpm packages for Linux, alongside self-contained CLI archives.
- Define the 0.4.0 release target scope as Windows 10/11 x64 and XWayland sessions on Ubuntu 24.04+, Debian 13, and Fedora 43; native Wayland, Alpine, NixOS, Flatpak, Linux ARM64, and macOS are outside that scope.
- Require system `ffmpeg` and `ffprobe` on Linux, declare `ffmpeg`/`ffmpeg-free` package dependencies for deb/rpm, and disable irreversible deletion there until Unix physical-directory identity checks provide the same safety guarantee as Windows.
- **CLI `delete` now moves the cache to the application trash by default instead of deleting it permanently.** Permanent deletion requires the new `--permanent` flag, both paths ask for confirmation unless `--yes` is passed, and a non-interactive shell without `--yes` refuses to act. This closes the gap where the GUI deleted safely but one CLI command destroyed data with no confirmation.
- Accept `av`-prefixed ids (for example `av170001`) wherever the CLI takes an avid.
- Size buttons to their content so Chinese labels are never clipped when font metrics change.
- Copy both DASH streams when audio is AAC; for other codecs, copy video and convert only audio to AAC for player compatibility.
- Skip redundant video-duration probing when reliable duration metadata is already available.
- Set desktop playback-artifact retention to 30 days and the default size limit to 10 GB.
- Limit configurable playback-artifact retention to five years and capacity to 128 GB.
- Keep valid metadata-backed legacy trash entries manageable after the cache root is relocated; missing-metadata legacy entries remain excluded from normal cleanup.
- Keep v1 trash entries untouched in older builds that do not understand their identity format; use a v1-aware build to restore or purge them.
- Run startup cleanup after the renderer mounts, and refresh storage statistics after scans and trash mutations.
- Use one checked-in FFmpeg manifest for Windows runtime, CI, and Release, pinning the exact BtbN tag, asset, URL, and SHA-256 instead of a mutable latest download; Linux uses system `ffmpeg` and `ffprobe`.
- Prefer the verified bundle on Windows; Linux requires FFmpeg from the system `PATH`.

### Fixed

- Keep entries that survived a trash purge undoable; previously purging the trash cleared the entire undo list even for entries that were skipped or failed before deletion started.
- Reject cache entries whose JSON avid does not match their parent cache directory before any delete operation can target them.
- Avoid cache misses caused only by title or other display-metadata changes.
- Discard generated artifacts when their source media changes during FFmpeg processing.
- Prevent cleanup from deleting unmanaged files, reparse-point targets, or another process's active build artifact.
- Tolerate artifacts disappearing while another application instance is concurrently inspecting or clearing the cache.
- Reject incomplete FFmpeg downloads and extractions instead of reusing partial installations.
- Treat missing required metadata and out-of-range timestamps as invalid entries without aborting the full scan.
- Keep desktop delete and undo state serialized and bind undo batches to their original cache root.
- Prevent the desktop playback queue from advancing more than one item concurrently.
- Clear the Host's cached index after the persisted cache root or incomplete-cache setting changes.
- Reject zero-byte playback streams and serialize FFmpeg bootstrap across application processes.
- Validate persisted enum settings and return failure exit codes when CLI targets do not exist.
- Release per-artifact synchronization objects after playback materialization completes.
- Reject release versions that do not match the repository version metadata.
- Revalidate cleanup candidates after acquiring their artifact lock so a concurrently reused file is not deleted.
- Include managed build artifacts consistently in statistics, cleanup previews, cleanup results, and confirmed clear operations while preserving active builds.
- Tolerate concurrent transcode artifact build-to-final promotion during storage statistics.
- Prevent optional FFmpeg version metadata failures from breaking an otherwise successful initialization.
- Show cancellable preparation progress while waiting for another process to generate the same playback artifact, without showing it for an uncontended cache hit.
- Serialize trash move, restore, statistics, and purge operations per normalized cache root across application processes.
- Make trash moves, restores, and permanent deletion handle-bound on Windows, with atomic metadata precommit and repeated identity validation.
- Keep a root-level purge journal bound to the physical directory volume/file ID until entry deletion commits, so missing internal state, process interruption, same-name replacement, and late journal cleanup all fail closed or retry safely.
- Report permanently freed trash capacity as the actual net byte reduction, excluding temporary marker, metadata-recovery, and journal overhead.
- Write settings through unique temporary files, flush them to disk, and replace the target file afterward.
- Bound post-playback artifact protection by count, age, and configured capacity so a long session cannot indefinitely defeat cleanup.
- Report incomplete transcode-cache clearing when locked artifacts remain instead of showing a false success.
- Reject reparse-point cache, trash, and media paths before scanning, moving, restoring, purging, or probing them.
- Bound Host diagnostic history to 100 events and each recorded field to 4,096 characters.
- Keep first-run FFmpeg preparation compatible with stock Windows PowerShell 5.1 by using its basic web-request parser.
- Keep CI and Release on the SDK feature band required by `global.json` instead of floating to an incompatible future band.
- Pin FFmpeg to a month-end build covered by the provider's long-retention policy instead of an expiring daily build.
- Skip entries with negative sizes or out-of-range durations without aborting the scan, and saturate aggregate size/duration totals.
- Add CI smoke coverage for the self-contained CLI and .NET Host, Electron on Ubuntu 24.04 Xvfb, and installed deb/rpm packages in Debian 13/Fedora 43 Xvfb containers; validate native release payloads and checksums before publishing them.
- Keep native save-dialog selection and Host export in one main-process IPC operation so the renderer cannot submit an unapproved overwrite path.
- Bound renderer-to-Host requests to 1 MiB and return a structured error before any Host response exceeds the aligned 64 MiB protocol ceiling.
- Preserve bounded session-protected playback artifacts during confirmed transcode-cache clearing.
- Tolerate managed artifact directories disappearing or becoming briefly inaccessible during concurrent statistics, cleanup, and clear operations.
- Reject a cache root that is itself a symbolic link or directory junction before scanning, measuring, trashing, purging, or permanently deleting data.
- Refuse permanent deletion when an avid cache tree contains a symbolic link or directory junction.

## [0.3.0] - 2026-07-11

### Added

- Observable cache scan reports with invalid-entry and inaccessible-directory statistics.
- Cancellation and progress feedback for desktop scanning and search-index creation.
- Page-level playback queue with explicit next-item and clear-queue actions.
- Managed playback artifacts with deterministic reuse, atomic generation, and bounded cleanup.
- Desktop application trash with batch safe deletion and undo support.
- Persistent cache path, search, and player preferences.
- Storage-size and last-updated summaries in the desktop cache list.
- Independent playback regression test project and Windows CI workflow.
- Self-contained Windows release packaging with SHA256 checksums.

### Changed

- FFmpeg initialization is delayed until media processing is actually required.
- Desktop batch playback no longer launches all selected pages at once.
- Desktop deletion moves caches to the application trash; CLI deletion was permanent in this historical release.

[Unreleased]: https://github.com/yuweiyang9611/BiliBiliLocalCacheManager/compare/v0.4.0-rc.1...HEAD
[0.4.0-rc.1]: https://github.com/yuweiyang9611/BiliBiliLocalCacheManager/releases/tag/v0.4.0-rc.1
[0.3.0]: https://github.com/yuweiyang9611/BiliBiliLocalCacheManager/releases/tag/v0.3.0
