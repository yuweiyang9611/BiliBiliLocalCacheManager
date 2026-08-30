using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;
using BiliBiliLocalCacheManager.Desktop.Host.Services;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using CoreContracts = BiliBiliLocalCacheManager.Core.Application.Contracts;
using PlaybackContracts = BiliBiliLocalCacheManager.Playback.Contracts;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed class DesktopHostApplication
{
    private const int MaximumSessionProtectedArtifactCount = 64;
    private static readonly TimeSpan MaximumSessionProtectionAge = TimeSpan.FromHours(6);

    private static readonly string[] SupportedMethods =
    [
        "health",
        "initialState",
        "settings.get",
        "settings.update",
        "scan",
        "cancel",
        "search",
        "storage.get",
        "artifacts.cleanup",
        "artifacts.clear",
        "trash.move",
        "trash.list",
        "trash.restore",
        "trash.purge",
        "play",
        "export",
        "diagnostics.export"
    ];

    private readonly CoreContracts.ICacheManager _cacheManager = new CacheManager();
    private readonly CoreContracts.ICacheTrashService _trashService = new FileSystemCacheTrashService();
    private readonly CoreContracts.ICacheStorageStatisticsService _storageStatisticsService =
        new FileSystemCacheStorageStatisticsService();
    private readonly PlaybackContracts.IPlaybackArtifactStore _artifactStore;
    private readonly CachePlaybackService _playbackService;
    private readonly PlaybackContracts.IFfmpegDiagnosticsProvider _ffmpegDiagnosticsProvider =
        new BundledFfmpegDiagnosticsProvider();
    private readonly PlaybackContracts.IFfmpegPrewarmService _ffmpegPrewarmService =
        new BundledFfmpegPrewarmService();
    private readonly SettingsStore _settingsStore;
    private readonly DiagnosticEventRecorder _eventRecorder = new();
    private readonly DiagnosticExporter _diagnosticExporter;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _artifactMaintenanceGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly object _artifactProtectionSync = new();
    private readonly LinkedList<SessionProtectedArtifact> _sessionProtectedArtifacts = new();
    private int _initialBackgroundWorkStarted;
    private int _backgroundArtifactCleanupQueued;
    private CacheIndex? _currentIndex;
    private string? _currentRoot;
    private bool _currentIncludeIncomplete;
    private DateTimeOffset? _lastScanCompletedAtUtc;

    public DesktopHostApplication()
    {
        var settingsPath = Environment.GetEnvironmentVariable(
            "BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH");
        var transcodeCacheRoot = Environment.GetEnvironmentVariable(
            "BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT");
        _settingsStore = new SettingsStore(
            string.IsNullOrWhiteSpace(settingsPath) ? null : settingsPath);
        _artifactStore = string.IsNullOrWhiteSpace(transcodeCacheRoot)
            ? PlaybackArtifactStore.Shared
            : new PlaybackArtifactStore(transcodeCacheRoot);
        _playbackService = new CachePlaybackService(_artifactStore);
        _diagnosticExporter = new DiagnosticExporter(
            _eventRecorder,
            _ffmpegDiagnosticsProvider,
            _artifactStore);
    }

    public event EventHandler<HostProgressEvent>? ProgressReported;

    public async Task<object?> DispatchAsync(
        string requestId,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        _eventRecorder.Record("Rpc", "Information", $"Started {method} ({requestId}).");
        try
        {
            var result = method switch
            {
                "health" => GetHealth(),
                "initialState" => await GetInitialStateAsync(parameters, cancellationToken),
                "settings.get" => GetSettings(),
                "settings.update" => UpdateSettings(parameters),
                "scan" => await ScanAsync(requestId, parameters, cancellationToken),
                "search" => await SearchAsync(requestId, parameters, cancellationToken),
                "storage.get" => await GetStorageAsync(parameters, cancellationToken),
                "artifacts.cleanup" => await CleanupArtifactsAsync(cancellationToken),
                "artifacts.clear" => await ClearArtifactsAsync(parameters, cancellationToken),
                "trash.move" => await MoveToTrashAsync(parameters, cancellationToken),
                "trash.list" => await ListTrashAsync(parameters, cancellationToken),
                "trash.restore" => await RestoreTrashAsync(parameters, cancellationToken),
                "trash.purge" => await PurgeTrashAsync(parameters, cancellationToken),
                "play" => await PlayAsync(requestId, parameters, cancellationToken),
                "export" => await ExportAsync(requestId, parameters, cancellationToken),
                "diagnostics.export" => await ExportDiagnosticsAsync(parameters, cancellationToken),
                _ => throw new RpcException(
                    "method_not_found",
                    $"Unknown RPC method '{method}'.",
                    new { method, supportedMethods = SupportedMethods })
            };
            _eventRecorder.Record("Rpc", "Information", $"Completed {method} ({requestId}).");
            return result;
        }
        catch (OperationCanceledException)
        {
            _eventRecorder.Record("Rpc", "Information", $"Cancelled {method} ({requestId}).");
            throw;
        }
        catch (Exception exception)
        {
            _eventRecorder.Record(
                "Rpc",
                "Error",
                $"Failed {method} ({requestId}): {exception.Message}",
                exception);
            throw;
        }
    }

    private object GetHealth()
    {
        var ffmpeg = _ffmpegDiagnosticsProvider.GetSnapshot();
        var warnings = new List<string>();
        if (!ffmpeg.IsInitialized)
        {
            warnings.Add("FFmpeg has not been initialized yet.");
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            warnings.Add("This operating system is outside the supported desktop scope.");
        }

        return new
        {
            service = "BiliBiliLocalCacheManager.Desktop.Host",
            status = warnings.Count == 0 ? "ok" : "degraded",
            protocolVersion = 1,
            version = ReadVersion(),
            runtime = RuntimeInformation.FrameworkDescription,
            platform = RuntimeInformation.RuntimeIdentifier,
            ffmpeg = ffmpeg.IsInitialized
                ? $"{ffmpeg.Source}: {ffmpeg.Version ?? "ready"}"
                : "not initialized",
            warnings,
            processId = Environment.ProcessId,
            runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            supportedMethods = SupportedMethods,
            capabilities = GetCapabilities()
        };
    }

    private Task<object> GetInitialStateAsync(
        JsonElement _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settingsStore.GetState();

        var result = new
        {
            protocolVersion = 1,
            settings = ToWireSettings(settings.Settings),
            settingsState = ToWireSettingsState(settings),
            // A previous non-persisting validation scan can target a different
            // root than the saved settings. Never replay that process-local index
            // after a renderer reload without an explicit root binding.
            items = Array.Empty<CacheDto>(),
            storage = CreateUnloadedStorage(settings.Settings),
            trash = Array.Empty<TrashEntryDto>(),
            capabilities = GetCapabilities()
        };
        QueueInitialBackgroundWork();
        return Task.FromResult<object>(result);
    }

    private object GetSettings()
    {
        var state = _settingsStore.GetState();
        return ToWireSettings(state.Settings);
    }

    private object UpdateSettings(JsonElement parameters)
    {
        var before = _settingsStore.GetState().Settings;
        var state = _settingsStore.Update(parameters);
        if ((!PathsEqual(before.RootPath, state.Settings.RootPath) ||
             before.IncludeIncomplete != state.Settings.IncludeIncomplete) &&
            !CurrentIndexMatchesSettings(state.Settings))
        {
            ClearCurrentIndex();
        }

        return ToWireSettings(state.Settings);
    }

    private async Task<object> ScanAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetState().Settings;
        var root = ResolveRequiredRoot(parameters, settings);
        var includeIncomplete = parameters.OptionalBoolean("includeIncomplete") ??
                                settings.IncludeIncomplete;
        var persistSettings = parameters.OptionalBoolean("persistSettings") ?? true;
        var maxReportedIssues = parameters.OptionalInt32("maxReportedIssues") ?? 100;
        if (maxReportedIssues is < 0 or > 10_000)
        {
            throw new RpcException(
                "invalid_params",
                "maxReportedIssues must be between 0 and 10000.");
        }

        var options = new CacheIndexBuildOptions
        {
            IncludeIncompleteEntries = includeIncomplete,
            MaxReportedIssues = maxReportedIssues
        };
        var progress = new InlineProgress<CacheScanProgress>(value =>
            ReportProgress(new HostProgressEvent(
                requestId,
                "scan",
                "scanning",
                Current: value.ProcessedAvidDirectories,
                Message: value.CurrentPath,
                Details: new
                {
                    value.ProcessedAvidDirectories,
                    value.ProcessedSegmentDirectories,
                    value.IncludedEntries
                })));

        var report = await Task.Run(
            () => _cacheManager.BuildIndexWithReport(
                root,
                options,
                cancellationToken,
                progress),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var completedAt = DateTimeOffset.UtcNow;
        lock (_stateSync)
        {
            _currentIndex = report.Index;
            _currentRoot = root;
            _currentIncludeIncomplete = includeIncomplete;
            _lastScanCompletedAtUtc = completedAt;
        }

        if (persistSettings)
        {
            TryPersistScanSettings(root, includeIncomplete);
        }
        var items = MapCaches(report.Index.VideoCaches, cancellationToken);
        ReportProgress(new HostProgressEvent(
            requestId,
            "scan",
            "completed",
            Percentage: 100,
            Current: items.Count,
            Total: items.Count));

        return new
        {
            rootPath = root,
            includeIncomplete,
            report.ScannedAvidDirectories,
            report.ScannedSegmentDirectories,
            report.IncludedEntries,
            report.SkippedIncompleteEntries,
            report.InvalidEntries,
            report.InaccessibleDirectories,
            report.HasWarnings,
            items,
            completedAtUtc = completedAt
        };
    }

    private async Task<object> SearchAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var keyword = parameters.OptionalString("keyword") ?? string.Empty;
        var settings = _settingsStore.GetState().Settings;
        var root = ResolveRequiredRoot(parameters, settings);
        var includeIncomplete = parameters.OptionalBoolean("includeIncomplete") ??
                                settings.IncludeIncomplete;
        var index = await ResolveIndexAsync(
            requestId,
            "search",
            root,
            includeIncomplete,
            cancellationToken);

        var options = new CacheSearchOptions
        {
            Keyword = keyword,
            SplitKeywords = parameters.OptionalBoolean("splitKeywords") ?? settings.SplitKeywords,
            RequireAllKeywords = !(parameters.OptionalBoolean("anyKeywords") ?? settings.AnyKeywords),
            CaseSensitive = parameters.OptionalBoolean("caseSensitive") ?? settings.CaseSensitive,
            MatchMode = ParseWireMatchMode(parameters.OptionalString("matchMode"), settings.MatchMode),
            Scope = ParseSearchScope(parameters, settings)
        };

        cancellationToken.ThrowIfCancellationRequested();
        var matches = string.IsNullOrWhiteSpace(keyword)
            ? index.VideoCaches
            : index.Search(options);
        return MapCaches(matches, cancellationToken);
    }

    private async Task<object> GetStorageAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetState().Settings;
        var cleanupOptions = CreateCleanupOptions(settings);
        var rawRoot = parameters.OptionalString("rootPath") ?? settings.RootPath;
        string? root = null;
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawRoot))
        {
            root = Path.GetFullPath(rawRoot);
            if (!Directory.Exists(root))
            {
                errors.Add($"Cache root does not exist: {root}");
            }
        }
        else
        {
            errors.Add("No cache root is configured.");
        }

        CacheStorageStatistics? originalCache = null;
        CacheTrashStatistics? trash = null;
        PlaybackArtifactCacheStatistics? transcodeCache = null;
        PlaybackArtifactCleanupPreview? transcodePreview = null;
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root is not null && Directory.Exists(root))
            {
                try
                {
                    originalCache = _storageStatisticsService.GetStatistics(root, cancellationToken);
                    if (originalCache.FailedEntryCount > 0)
                    {
                        errors.Add($"{originalCache.FailedEntryCount} cache entries could not be measured.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    errors.Add($"Cache statistics failed: {exception.Message}");
                }

                try
                {
                    trash = _trashService.GetStatistics(root, cancellationToken);
                    if (trash.FailedEntryCount > 0)
                    {
                        errors.Add($"{trash.FailedEntryCount} trash entries could not be measured.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    errors.Add($"Trash statistics failed: {exception.Message}");
                }
            }

            try
            {
                transcodeCache = _artifactStore.GetStatistics();
                cancellationToken.ThrowIfCancellationRequested();
                transcodePreview = _artifactStore.PreviewCleanup(cleanupOptions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add($"Transcode cache statistics failed: {exception.Message}");
            }
        }, cancellationToken);

        var managedTotal = SaturatingAdd(
            SaturatingAdd(originalCache?.TotalBytes ?? 0, trash?.TotalBytes ?? 0),
            transcodeCache?.TotalBytes ?? 0);
        var reclaimable = SaturatingAdd(
            trash?.TotalBytes ?? 0,
            transcodePreview?.ReclaimableBytes ?? 0);
        return new
        {
            originalCache = new
            {
                bytes = originalCache?.TotalBytes ?? 0,
                itemCount = originalCache?.ManagedEntryCount ?? 0,
                path = root
            },
            transcodeCache = new
            {
                bytes = transcodeCache?.TotalBytes ?? 0,
                itemCount = transcodeCache?.FileCount ?? 0,
                path = transcodeCache?.RootDirectory ?? _artifactStore.RootDirectory
            },
            trash = new
            {
                bytes = trash?.TotalBytes ?? 0,
                itemCount = trash?.ManagedEntryCount ?? 0,
                path = trash?.TrashDirectory
            },
            totalBytes = managedTotal,
            lastMaintenanceSummary = errors.Count == 0
                ? $"Policy can reclaim {reclaimable} bytes."
                : string.Join("; ", errors)
        };
    }

    private async Task<object> CleanupArtifactsAsync(CancellationToken cancellationToken)
    {
        var cleanupOptions = CreateCleanupOptions(_settingsStore.GetState().Settings);
        var result = await RunArtifactMaintenanceAsync(
            () => _artifactStore.Cleanup(cleanupOptions),
            cancellationToken);
        RecordArtifactMaintenance("Manual policy cleanup", result);
        return ToWireArtifactCleanupResult(result);
    }

    private async Task<object> ClearArtifactsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.OptionalBoolean("confirmed") != true)
        {
            throw new RpcException(
                "confirmation_required",
                "artifacts.clear requires params.confirmed=true because it is irreversible.");
        }

        var result = await RunArtifactMaintenanceAsync(
            () => _artifactStore.Cleanup(CreateClearOptions()),
            cancellationToken);
        RecordArtifactMaintenance("Manual cache clear", result);
        return ToWireArtifactCleanupResult(result);
    }

    private async Task<object> MoveToTrashAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var root = ResolveRequiredRoot(parameters, _settingsStore.GetState().Settings);
        var avids = ParseRequiredStringArray(parameters, "avids", maximumCount: 1000)
            .Select(ParseAvid)
            .Distinct()
            .ToArray();
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() =>
            {
                var moved = new List<string>();
                var failed = new List<string>();
                foreach (var avid in avids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = _trashService.MoveToTrash(root, avid);
                    var avidText = avid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (item.Succeeded)
                    {
                        moved.Add(avidText);
                    }
                    else
                    {
                        failed.Add(avidText);
                    }
                }

                return new { moved, failed };
            }, cancellationToken);
            if (result.moved.Count > 0)
            {
                ClearCurrentIndex();
            }

            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<object> ListTrashAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var root = ResolveRequiredRoot(parameters, _settingsStore.GetState().Settings);
        return await Task.Run<object>(
            () => MapTrashEntries(_trashService.ListEntries(root, cancellationToken)),
            cancellationToken);
    }

    private async Task<object> RestoreTrashAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var root = ResolveRequiredRoot(parameters, _settingsStore.GetState().Settings);
        var entryIds = ParseRequiredStringArray(parameters, "entryIds", maximumCount: 1000)
            .Distinct(PathComparer)
            .ToArray();
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() =>
            {
                var entries = _trashService.ListEntries(root, cancellationToken)
                    .ToDictionary(entry => entry.TrashPath, PathComparer);
                var restored = new List<string>();
                var failed = new List<string>();
                foreach (var entryId in entryIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entries.TryGetValue(entryId, out var entry) || !entry.IsRestorable)
                    {
                        failed.Add(entryId);
                        continue;
                    }

                    var operation = _trashService.Restore(root, entry.Avid, entry.TrashPath);
                    if (operation.Succeeded)
                    {
                        restored.Add(entryId);
                    }
                    else
                    {
                        failed.Add(entryId);
                    }
                }

                return new { restored, failed };
            }, cancellationToken);
            if (result.restored.Count > 0)
            {
                ClearCurrentIndex();
            }

            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<object> PurgeTrashAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new RpcException(
                "unsupported_platform",
                "Permanent trash purge is disabled outside Windows until Unix physical-directory safety is implemented.",
                new { capability = "trashPurge", supported = false });
        }

        if (parameters.OptionalBoolean("confirmed") != true)
        {
            throw new RpcException(
                "confirmation_required",
                "trash.purge requires params.confirmed=true because it is irreversible.");
        }

        var root = ResolveExplicitRequiredRoot(parameters);
        var requestedIds = ParseRequiredStringArray(parameters, "entryIds", maximumCount: 10_000)
            .Distinct(PathComparer)
            .ToArray();
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run<object>(() =>
            {
                var selectedIds = requestedIds.ToHashSet(PathComparer);
                try
                {
                    _trashService.Purge(
                        root,
                        includeUntrustedLegacyEntries: false,
                        expectedEntryIds: requestedIds);
                }
                catch (CacheTrashSnapshotMismatchException exception)
                {
                    throw new RpcException(
                        "unsupported_operation",
                        "The trash contents changed or the selection is incomplete. " +
                        "Reload the trash and confirm permanent deletion again.",
                        new
                        {
                            reason = "trash_snapshot_changed",
                            exception.ExpectedEntryCount,
                            exception.ActualEntryCount
                        });
                }

                var remaining = _trashService.ListEntries(root, cancellationToken)
                    .Select(entry => entry.TrashPath)
                    .ToHashSet(PathComparer);
                var purged = selectedIds.Where(id => !remaining.Contains(id)).ToArray();
                var failed = selectedIds.Where(remaining.Contains).ToArray();
                return new { purged, failed };
            }, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<object> PlayAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetState().Settings;
        var root = ResolveRequiredRoot(parameters, settings);
        var includeIncomplete = parameters.OptionalBoolean("includeIncomplete") ??
                                settings.IncludeIncomplete;
        var targets = ParseSelectionTargets(parameters);
        var player = ParseWirePlayerPreference(
            parameters.OptionalString("playerPreference"),
            settings.PreferredPlayer);
        var index = await ResolveIndexAsync(
            requestId,
            "play",
            root,
            includeIncomplete,
            cancellationToken);
        var pages = new List<PlaybackTarget>();
        var failed = new List<string>();
        foreach (var target in targets)
        {
            if (!index.ByAvid.TryGetValue(target.Avid, out var cache))
            {
                failed.Add(target.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            var pageIndexes = target.PageIndexes is { Count: > 0 }
                ? target.PageIndexes
                : _playbackService.CreatePagePlans(cache).Select(plan => plan.PageIndex).ToArray();
            pages.AddRange(pageIndexes
                .Distinct()
                .OrderBy(pageIndex => pageIndex)
                .Select(pageIndex => new PlaybackTarget(cache, pageIndex)));
        }

        var queued = 0;
        var launchedArtifactPaths = new List<string>();
        for (var indexInQueue = 0; indexInQueue < pages.Count; indexInQueue++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = pages[indexInQueue];
            var ordinal = indexInQueue + 1;
            var progress = new InlineProgress<PlaybackPreparationProgress>(value =>
                ReportProgress(new HostProgressEvent(
                    requestId,
                    "play",
                    value.Stage,
                    value.Percentage,
                    Current: ordinal,
                    Total: pages.Count,
                    Message: $"av{target.Cache.Avid} P{target.PageIndex}",
                    Details: new
                    {
                        elapsedMilliseconds = value.Elapsed.TotalMilliseconds,
                        estimatedRemainingMilliseconds = value.EstimatedRemaining?.TotalMilliseconds
                    })));
            var result = await _playbackService.PlayAsync(
                target.Cache,
                target.PageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new PlaybackLaunchOptions { PreferredPlayer = player },
                progress,
                cancellationToken);
            if (result.Succeeded)
            {
                queued++;
                if (!string.IsNullOrWhiteSpace(result.ManagedArtifactPath))
                {
                    ProtectLaunchedArtifact(result.ManagedArtifactPath);
                    launchedArtifactPaths.Add(result.ManagedArtifactPath);
                }
            }
            else
            {
                failed.Add($"{target.Cache.Avid}:{target.PageIndex}");
            }
        }

        if (queued > 0)
        {
            QueueBackgroundArtifactCleanup("Post-playback policy cleanup", launchedArtifactPaths);
        }

        return new { queued, failed };
    }

    private async Task<object> ExportAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetState().Settings;
        var root = ResolveRequiredRoot(parameters, settings);
        var includeIncomplete = parameters.OptionalBoolean("includeIncomplete") ??
                                settings.IncludeIncomplete;
        var selections = ParseSelectionTargets(parameters);
        var requestedOutputPath = Path.GetFullPath(parameters.RequireString("outputPath"));
        var requestedParent = Path.GetDirectoryName(requestedOutputPath);
        if (string.IsNullOrWhiteSpace(requestedParent) || !Directory.Exists(requestedParent))
        {
            throw new DirectoryNotFoundException($"Export destination directory not found: {requestedParent}");
        }

        var index = await ResolveIndexAsync(
            requestId,
            "export",
            root,
            includeIncomplete,
            cancellationToken);
        var requests = ExpandExportTargets(index, selections);
        if (requests.Count == 0)
        {
            throw new RpcException("not_found", "None of the requested export targets exist in the index.");
        }

        var destinationIsDirectory = requests.Count > 1 || Directory.Exists(requestedOutputPath);
        var destination = destinationIsDirectory
            ? CreateBatchExportDirectory(requestedOutputPath)
            : requestedOutputPath;
        var exported = new List<object>();
        var failures = new List<object>();
        for (var requestIndex = 0; requestIndex < requests.Count; requestIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = requests[requestIndex];
            if (!index.ByAvid.TryGetValue(target.Avid, out var cache))
            {
                failures.Add(new { target.Avid, target.SegmentKey, message = "Cache not found." });
                continue;
            }

            CachePlaybackPagePlan pagePlan;
            try
            {
                pagePlan = _playbackService.CreatePagePlan(cache, target.SegmentKey);
            }
            catch (Exception exception)
            {
                failures.Add(new { target.Avid, target.SegmentKey, message = exception.Message });
                continue;
            }

            if (!pagePlan.IsPlayable)
            {
                failures.Add(new
                {
                    target.Avid,
                    target.SegmentKey,
                    message = pagePlan.SelectedPlan.Message ?? pagePlan.Message ?? "Page is not playable."
                });
                continue;
            }

            var ordinal = requestIndex + 1;
            var progress = new InlineProgress<PlaybackPreparationProgress>(value =>
                ReportProgress(new HostProgressEvent(
                    requestId,
                    "export",
                    value.Stage,
                    value.Percentage,
                    Current: ordinal,
                    Total: requests.Count,
                    Message: $"av{target.Avid} P{pagePlan.PageIndex}",
                    Details: new
                    {
                        target.Avid,
                        pagePlan.PageIndex,
                        elapsedMilliseconds = value.Elapsed.TotalMilliseconds,
                        estimatedRemainingMilliseconds = value.EstimatedRemaining?.TotalMilliseconds
                    })));

            var materialization = await _playbackService.MaterializeAsync(
                pagePlan.SelectedPlan,
                progress,
                cancellationToken);
            if (!materialization.Succeeded || string.IsNullOrWhiteSpace(materialization.OutputPath))
            {
                failures.Add(new { target.Avid, target.SegmentKey, message = materialization.Message });
                continue;
            }

            var outputPath = destinationIsDirectory
                ? PortableFileNaming.EnsureUnique(
                    destination,
                    PortableFileNaming.Build(
                        cache.Title,
                        cache.Avid,
                        pagePlan.PageIndex,
                        pagePlan.PartName,
                        cache.Segments.Select(segment => segment.PageIndex).Distinct().Count() > 1),
                    ".mp4")
                : destination;
            await CopyAtomicallyAsync(materialization.OutputPath, outputPath, cancellationToken);
            exported.Add(new
            {
                target.Avid,
                pagePlan.PageIndex,
                pagePlan.PartName,
                outputPath,
                materialization.MaterializerName
            });
        }

        if (exported.Count == 0)
        {
            throw new RpcException(
                "operation_failed",
                "No media target could be exported.",
                new { failures });
        }

        return new
        {
            outputPath = destination,
            exportedCount = exported.Count,
            failedCount = failures.Count,
            failures
        };
    }

    private async Task<object> ExportDiagnosticsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var destination = parameters.RequireString("outputPath");
        var sessionRootPath = parameters.OptionalString("rootPath");
        return await _diagnosticExporter.ExportAsync(
            destination,
            _settingsStore.GetState(),
            GetSessionState(),
            sessionRootPath,
            cancellationToken);
    }

    private async Task<CacheIndex> ResolveIndexAsync(
        string requestId,
        string operation,
        string root,
        bool includeIncomplete,
        CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            if (_currentIndex is not null &&
                PathsEqual(_currentRoot, root) &&
                _currentIncludeIncomplete == includeIncomplete)
            {
                return _currentIndex;
            }
        }

        var progress = new InlineProgress<CacheScanProgress>(value =>
            ReportProgress(new HostProgressEvent(
                requestId,
                operation,
                "indexing",
                Current: value.ProcessedAvidDirectories,
                Message: value.CurrentPath,
                Details: new
                {
                    value.ProcessedAvidDirectories,
                    value.ProcessedSegmentDirectories,
                    value.IncludedEntries
                })));
        var options = new CacheIndexBuildOptions
        {
            IncludeIncompleteEntries = includeIncomplete
        };
        var report = await Task.Run(
            () => _cacheManager.BuildIndexWithReport(
                root,
                options,
                cancellationToken,
                progress),
            cancellationToken);
        lock (_stateSync)
        {
            _currentIndex = report.Index;
            _currentRoot = root;
            _currentIncludeIncomplete = includeIncomplete;
            _lastScanCompletedAtUtc = DateTimeOffset.UtcNow;
            return _currentIndex;
        }
    }

    private IReadOnlyList<CacheDto> MapCaches(
        IEnumerable<BiliVideoCache> caches,
        CancellationToken cancellationToken)
    {
        var mapped = new List<CacheDto>();
        foreach (var cache in caches.OrderByDescending(GetLastUpdatedUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = cache.Segments
                .OrderBy(segment => segment.PageIndex)
                .ThenBy(segment => segment.SegmentDirectory, PathComparer)
                .Select(MapSegment)
                .ToArray();
            mapped.Add(new CacheDto(
                cache.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cache.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cache.Title,
                cache.Bvid ?? string.Empty,
                cache.OwnerName ?? string.Empty,
                cache.TotalDuration.TotalSeconds,
                cache.Segments.Count,
                cache.TotalSize,
                cache.IsAllCompleted,
                GetLastUpdatedUtc(cache) == DateTimeOffset.MinValue
                    ? null
                    : GetLastUpdatedUtc(cache),
                segments));
        }

        return mapped;
    }

    private SegmentDto MapSegment(BiliSegment segment)
    {
        CachePlaybackPlan? plan = null;
        try
        {
            plan = _playbackService.CreatePlan(segment);
        }
        catch (Exception)
        {
        }

        var segmentKey = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(segment.SegmentDirectory));
        return new SegmentDto(
            $"{segment.Avid}:{segment.PageIndex}:{segmentKey}",
            segmentKey,
            segment.PageIndex,
            segment.PartName,
            plan?.StructureKind ?? "Unknown",
            plan?.MaterialKind.ToString() ?? "Unavailable",
            segment.TotalBytes,
            segment.TotalDuration.TotalSeconds,
            plan?.IsPlayable == true,
            segment.SegmentDirectory);
    }

    private static CacheSearchScope ParseSearchScope(
        JsonElement parameters,
        DesktopSettings settings)
    {
        var values = parameters.OptionalArray("scope");
        if (values.Count > 0)
        {
            var scope = CacheSearchScope.None;
            foreach (var value in values)
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new RpcException("invalid_params", "Every scope item must be a string.");
                }

                scope |= value.GetString()?.Trim().ToLowerInvariant() switch
                {
                    "title" => CacheSearchScope.Title,
                    "partname" or "part" => CacheSearchScope.PartName,
                    "ownername" or "owner" => CacheSearchScope.OwnerName,
                    "bvid" => CacheSearchScope.Bvid,
                    "avid" => CacheSearchScope.Avid,
                    _ => throw new RpcException(
                        "invalid_params",
                        $"Unsupported search scope '{value.GetString()}'.")
                };
            }

            if (scope == CacheSearchScope.None)
            {
                throw new RpcException("invalid_params", "Search scope must not be empty.");
            }

            return scope;
        }

        var result = CacheSearchScope.Title;
        if (parameters.OptionalBoolean("includePartName") ?? settings.IncludePartName)
        {
            result |= CacheSearchScope.PartName;
        }

        if (parameters.OptionalBoolean("includeOwnerName") ?? settings.IncludeOwnerName)
        {
            result |= CacheSearchScope.OwnerName;
        }

        if (parameters.OptionalBoolean("includeBvid") ?? settings.IncludeBvid)
        {
            result |= CacheSearchScope.Bvid;
        }

        if (parameters.OptionalBoolean("includeAvid") ?? settings.IncludeAvid)
        {
            result |= CacheSearchScope.Avid;
        }

        return result;
    }

    private static IReadOnlyList<SelectionTargetRequest> ParseSelectionTargets(JsonElement parameters)
    {
        var targetElements = parameters.OptionalArray("targets");
        if (targetElements.Count is 0 or > 1000)
        {
            throw new RpcException("invalid_params", "targets must contain between 1 and 1000 items.");
        }

        var targets = targetElements.Select(target =>
        {
            if (target.ValueKind != JsonValueKind.Object)
            {
                throw new RpcException("invalid_params", "Every selection target must be an object.");
            }

            var avid = ParseAvid(target.RequireString("avid"));
            var pageElements = target.OptionalArray("pageIndexes");
            IReadOnlyList<int>? pageIndexes = null;
            if (pageElements.Count > 0)
            {
                if (pageElements.Count > 10_000)
                {
                    throw new RpcException("invalid_params", "pageIndexes may not exceed 10000 items.");
                }

                pageIndexes = pageElements.Select(page =>
                {
                    if (page.ValueKind != JsonValueKind.Number ||
                        !page.TryGetInt32(out var pageIndex) ||
                        pageIndex < 0)
                    {
                        throw new RpcException(
                            "invalid_params",
                            "Every pageIndexes item must be a non-negative integer.");
                    }

                    return pageIndex;
                }).Distinct().ToArray();
            }

            return new SelectionTargetRequest(avid, pageIndexes);
        }).Distinct().ToArray();
        return targets;
    }

    private IReadOnlyList<ExportTargetRequest> ExpandExportTargets(
        CacheIndex index,
        IReadOnlyList<SelectionTargetRequest> selections)
    {
        var targets = new List<ExportTargetRequest>();
        foreach (var selection in selections)
        {
            if (!index.ByAvid.TryGetValue(selection.Avid, out var cache))
            {
                continue;
            }

            var pageIndexes = selection.PageIndexes is { Count: > 0 }
                ? selection.PageIndexes
                : _playbackService.CreatePagePlans(cache).Select(plan => plan.PageIndex).ToArray();
            targets.AddRange(pageIndexes.Select(pageIndex => new ExportTargetRequest(
                selection.Avid,
                pageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        return targets.Distinct().ToArray();
    }

    private static IReadOnlyList<string> ParseRequiredStringArray(
        JsonElement parameters,
        string propertyName,
        int maximumCount)
    {
        var values = ParseOptionalStringArray(parameters, propertyName, maximumCount);
        if (values.Count == 0)
        {
            throw new RpcException("invalid_params", $"{propertyName} must contain at least one item.");
        }

        return values;
    }

    private static IReadOnlyList<string> ParseOptionalStringArray(
        JsonElement parameters,
        string propertyName,
        int maximumCount)
    {
        var elements = parameters.OptionalArray(propertyName);
        if (elements.Count > maximumCount)
        {
            throw new RpcException(
                "invalid_params",
                $"{propertyName} may not exceed {maximumCount} items.");
        }

        return elements.Select(element =>
        {
            if (element.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(element.GetString()))
            {
                throw new RpcException(
                    "invalid_params",
                    $"Every {propertyName} item must be a non-empty string.");
            }

            return element.GetString()!.Trim();
        }).ToArray();
    }

    private static long ParseAvid(string value)
    {
        var normalized = value.StartsWith("av", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
        if (!long.TryParse(
                normalized,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var avid) || avid < 0)
        {
            throw new RpcException("invalid_params", $"Invalid avid '{value}'.");
        }

        return avid;
    }

    private static CacheSearchMatchMode ParseWireMatchMode(
        string? value,
        CacheSearchMatchMode defaultValue)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => defaultValue,
            "contains" => CacheSearchMatchMode.Contains,
            "prefix" => CacheSearchMatchMode.StartsWith,
            "exact" => CacheSearchMatchMode.Equals,
            _ => throw new RpcException("invalid_params", $"Unsupported matchMode '{value}'.")
        };
    }

    private static PlaybackPlayerPreference ParseWirePlayerPreference(
        string? value,
        PlaybackPlayerPreference defaultValue)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => defaultValue,
            "system" => PlaybackPlayerPreference.SystemDefaultFirst,
            "mpv" => PlaybackPlayerPreference.Mpv,
            "vlc" => PlaybackPlayerPreference.Vlc,
            _ => throw new RpcException("invalid_params", $"Unsupported playerPreference '{value}'.")
        };
    }

    private static object ToWireSettings(DesktopSettings settings)
    {
        return new
        {
            settings.RootPath,
            settings.RememberRootPath,
            settings.ScanOnStartup,
            settings.IncludeIncomplete,
            settings.Keyword,
            settings.SplitKeywords,
            settings.AnyKeywords,
            settings.IncludePartName,
            settings.IncludeOwnerName,
            settings.IncludeBvid,
            settings.IncludeAvid,
            settings.CaseSensitive,
            matchMode = settings.MatchMode switch
            {
                CacheSearchMatchMode.StartsWith => "prefix",
                CacheSearchMatchMode.Equals => "exact",
                _ => "contains"
            },
            playerPreference = settings.PreferredPlayer switch
            {
                PlaybackPlayerPreference.Mpv => "mpv",
                PlaybackPlayerPreference.Vlc => "vlc",
                _ => "system"
            },
            settings.TranscodeCacheRetentionDays,
            settings.TranscodeCacheMaxSizeGigabytes
        };
    }

    private static object ToWireSettingsState(SettingsState state)
    {
        return new SettingsStateDto(
            state.CanSave,
            state.SourceSchemaVersion,
            state.Message);
    }

    private object CreateUnloadedStorage(DesktopSettings settings)
    {
        var rootPath = string.IsNullOrWhiteSpace(settings.RootPath)
            ? null
            : settings.RootPath;
        return new
        {
            originalCache = new
            {
                bytes = 0L,
                itemCount = 0,
                path = rootPath
            },
            transcodeCache = new
            {
                bytes = 0L,
                itemCount = 0,
                path = _artifactStore.RootDirectory
            },
            trash = new
            {
                bytes = 0L,
                itemCount = 0,
                path = (string?)null
            },
            totalBytes = 0L,
            lastMaintenanceSummary = "Storage statistics have not been loaded."
        };
    }

    private static IReadOnlyList<TrashEntryDto> MapTrashEntries(
        IEnumerable<CacheTrashEntry> entries)
    {
        return entries.Select(entry => new TrashEntryDto(
                entry.TrashPath,
                entry.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"av{entry.Avid}",
                entry.TotalBytes,
                entry.DeletedAtUtc,
                entry.OriginalPath))
            .ToArray();
    }

    private static string CreateBatchExportDirectory(string requestedOutputPath)
    {
        if (Directory.Exists(requestedOutputPath))
        {
            return requestedOutputPath;
        }

        var parent = Path.GetDirectoryName(requestedOutputPath) ??
                     throw new IOException("The export output path has no parent directory.");
        var baseName = Path.GetFileNameWithoutExtension(requestedOutputPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "cache-export";
        }

        var candidate = Path.Combine(parent, baseName);
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(parent, $"{baseName} ({suffix})");
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static async Task CopyAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (PathsEqual(source, destination))
        {
            throw new IOException("The export source and destination are the same file.");
        }

        var parent = Path.GetDirectoryName(destination) ??
                     throw new IOException("The export destination has no parent directory.");
        var temporaryPath = Path.Combine(
            parent,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.writing");
        try
        {
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             128 * 1024,
                             useAsync: true))
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private void TryPersistScanSettings(string root, bool includeIncomplete)
    {
        try
        {
            var settings = _settingsStore.GetState().Settings;
            var patch = settings.RememberRootPath
                ? JsonSerializer.SerializeToElement(new
                {
                    rootPath = root,
                    includeIncomplete
                })
                : JsonSerializer.SerializeToElement(new
                {
                    includeIncomplete
                });
            _settingsStore.Update(patch);
        }
        catch (Exception exception)
        {
            _eventRecorder.Record(
                "Settings",
                "Warning",
                $"Could not persist scan settings: {exception.Message}",
                exception);
        }
    }

    private string ResolveRequiredRoot(JsonElement parameters, DesktopSettings settings)
    {
        var rawRoot = parameters.OptionalString("rootPath") ?? settings.RootPath;
        if (string.IsNullOrWhiteSpace(rawRoot))
        {
            throw new RpcException(
                "invalid_params",
                "A cache root is required in params.rootPath or settings.rootPath.");
        }

        var root = Path.GetFullPath(rawRoot);
        if (!Directory.Exists(root))
        {
            throw new RpcException(
                "not_found",
                $"Cache root directory not found: {root}",
                new { rootPath = root });
        }

        return root;
    }

    private static string ResolveExplicitRequiredRoot(JsonElement parameters)
    {
        var root = Path.GetFullPath(parameters.RequireString("rootPath"));
        if (!Directory.Exists(root))
        {
            throw new RpcException(
                "not_found",
                $"Cache root directory not found: {root}",
                new { rootPath = root });
        }

        return root;
    }

    private object GetSessionState()
    {
        lock (_stateSync)
        {
            return new
            {
                rootConfigured = !string.IsNullOrWhiteSpace(_currentRoot),
                includeIncomplete = _currentIndex is null ? (bool?)null : _currentIncludeIncomplete,
                cacheCount = _currentIndex?.VideoCaches.Count ?? 0,
                lastScanCompletedAtUtc = _lastScanCompletedAtUtc
            };
        }
    }

    private bool CurrentIndexMatchesSettings(DesktopSettings settings)
    {
        lock (_stateSync)
        {
            if (_currentIndex is null ||
                _currentIncludeIncomplete != settings.IncludeIncomplete)
            {
                return false;
            }

            // When the root is intentionally not persisted, the current index is
            // still the validated root for this process lifetime. Otherwise it is
            // reusable only when it matches the newly saved root.
            return !settings.RememberRootPath
                ? !string.IsNullOrWhiteSpace(_currentRoot)
                : PathsEqual(_currentRoot, settings.RootPath);
        }
    }

    private static object GetCapabilities()
    {
        return new
        {
            platform = OperatingSystem.IsWindows()
                ? "windows"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : "unsupported",
            scan = true,
            search = true,
            playback = true,
            exportMedia = true,
            diagnostics = true,
            nativeDialogs = false,
            nativeWayland = false,
            trashMove = true,
            trashRestore = true,
            trashPurge = OperatingSystem.IsWindows(),
            linuxTrashPurgeSafetyImplemented = false
        };
    }

    private async Task<PlaybackArtifactCleanupResult> RunArtifactMaintenanceAsync(
        Func<PlaybackArtifactCleanupResult> operation,
        CancellationToken cancellationToken)
    {
        await _artifactMaintenanceGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }, cancellationToken);
        }
        finally
        {
            _artifactMaintenanceGate.Release();
        }
    }

    private void QueueInitialBackgroundWork()
    {
        if (Interlocked.CompareExchange(ref _initialBackgroundWorkStarted, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(PrewarmFfmpegInBackgroundAsync);
        QueueBackgroundArtifactCleanup("Startup policy cleanup");
    }

    private async Task PrewarmFfmpegInBackgroundAsync()
    {
        try
        {
            var result = await _ffmpegPrewarmService.PrewarmAsync(CancellationToken.None);
            _eventRecorder.Record(
                "FFmpeg",
                result.Succeeded ? "Information" : "Warning",
                result.Succeeded
                    ? $"Background prewarm completed: {result.Message}"
                    : $"Background prewarm was not available: {result.Message}");
        }
        catch (Exception exception)
        {
            _eventRecorder.Record(
                "FFmpeg",
                "Warning",
                $"Background prewarm failed without interrupting the desktop session: {exception.Message}",
                exception);
        }
    }

    private void QueueBackgroundArtifactCleanup(
        string reason,
        IReadOnlyCollection<string>? immediatelyProtectedPaths = null)
    {
        if (immediatelyProtectedPaths is { Count: > 0 })
        {
            foreach (var path in immediatelyProtectedPaths)
            {
                ProtectLaunchedArtifact(path);
            }
        }

        if (Interlocked.CompareExchange(ref _backgroundArtifactCleanupQueued, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var cleanupOptions = CreateCleanupOptions(_settingsStore.GetState().Settings);
                var result = await RunArtifactMaintenanceAsync(
                    () => _artifactStore.Cleanup(cleanupOptions),
                    CancellationToken.None);
                RecordArtifactMaintenance(reason, result);
            }
            catch (Exception exception)
            {
                _eventRecorder.Record(
                    "Artifacts",
                    "Warning",
                    $"{reason} failed without interrupting the active operation: {exception.Message}",
                    exception);
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundArtifactCleanupQueued, 0);
            }
        });
    }

    private PlaybackArtifactCleanupOptions CreateCleanupOptions(DesktopSettings settings)
    {
        var options = settings.CreateCleanupOptions();
        options.ProtectedPaths = SnapshotSessionProtectedArtifacts(options.MaxTotalBytes);
        return options;
    }

    private PlaybackArtifactCleanupOptions CreateClearOptions() => new()
    {
        MaxAge = TimeSpan.Zero,
        MaxTotalBytes = 0,
        CapacityEvictionGracePeriod = TimeSpan.Zero,
        ProtectedPaths = SnapshotSessionProtectedArtifacts(long.MaxValue)
    };

    private void ProtectLaunchedArtifact(string path)
    {
        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        lock (_artifactProtectionSync)
        {
            for (var node = _sessionProtectedArtifacts.First; node is not null;)
            {
                var next = node.Next;
                if (PathComparer.Equals(node.Value.Path, normalizedPath))
                {
                    _sessionProtectedArtifacts.Remove(node);
                }

                node = next;
            }

            _sessionProtectedArtifacts.AddFirst(
                new SessionProtectedArtifact(normalizedPath, DateTimeOffset.UtcNow));
            TrimSessionProtection(DateTimeOffset.UtcNow);
        }
    }

    private IReadOnlyList<string> SnapshotSessionProtectedArtifacts(long capacityLimitBytes)
    {
        lock (_artifactProtectionSync)
        {
            var now = DateTimeOffset.UtcNow;
            TrimSessionProtection(now);
            var protectedPaths = new List<string>();
            long protectedBytes = 0;
            for (var node = _sessionProtectedArtifacts.First; node is not null;)
            {
                var next = node.Next;
                long length;
                try
                {
                    var file = new FileInfo(node.Value.Path);
                    if (!file.Exists)
                    {
                        _sessionProtectedArtifacts.Remove(node);
                        node = next;
                        continue;
                    }

                    length = file.Length;
                }
                catch
                {
                    _sessionProtectedArtifacts.Remove(node!);
                    node = next;
                    continue;
                }

                if (length > capacityLimitBytes || protectedBytes > capacityLimitBytes - length)
                {
                    _sessionProtectedArtifacts.Remove(node);
                    node = next;
                    continue;
                }

                protectedPaths.Add(node.Value.Path);
                protectedBytes = SaturatingAdd(protectedBytes, length);
                node = next;
            }

            return protectedPaths;
        }
    }

    private void TrimSessionProtection(DateTimeOffset now)
    {
        while (_sessionProtectedArtifacts.Last is { } last &&
               (now - last.Value.ProtectedAtUtc > MaximumSessionProtectionAge ||
                _sessionProtectedArtifacts.Count > MaximumSessionProtectedArtifactCount))
        {
            _sessionProtectedArtifacts.RemoveLast();
        }
    }

    private void RecordArtifactMaintenance(
        string reason,
        PlaybackArtifactCleanupResult result)
    {
        _eventRecorder.Record(
            "Artifacts",
            result.FailedFileCount == 0 ? "Information" : "Warning",
            $"{reason}: deleted {result.DeletedFileCount} files, freed {result.FreedBytes} bytes, " +
            $"{result.FailedFileCount} failures, {result.RemainingBytes} bytes remain.");
    }

    private static object ToWireArtifactCleanupResult(PlaybackArtifactCleanupResult result)
    {
        return new
        {
            deletedFileCount = result.DeletedFileCount,
            freedBytes = result.FreedBytes,
            failedFileCount = result.FailedFileCount,
            remainingBytes = result.RemainingBytes
        };
    }

    private void ClearCurrentIndex()
    {
        lock (_stateSync)
        {
            _currentIndex = null;
            _currentRoot = null;
            _lastScanCompletedAtUtc = null;
        }
    }

    private void ReportProgress(HostProgressEvent progress)
    {
        ProgressReported?.Invoke(this, progress);
    }

    private static DateTimeOffset GetLastUpdatedUtc(BiliVideoCache cache)
    {
        return cache.Segments.Count == 0
            ? DateTimeOffset.MinValue
            : cache.Segments.Max(segment => segment.UpdatedAt).ToUniversalTime();
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadVersion()
    {
        return typeof(DesktopHostApplication).Assembly
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                   .InformationalVersion ??
               typeof(DesktopHostApplication).Assembly.GetName().Version?.ToString() ??
               "unknown";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record SelectionTargetRequest(long Avid, IReadOnlyList<int>? PageIndexes);

    private sealed record PlaybackTarget(BiliVideoCache Cache, int PageIndex);

    private sealed record SessionProtectedArtifact(string Path, DateTimeOffset ProtectedAtUtc);

    private sealed record ExportTargetRequest(long Avid, string? SegmentKey);
}
