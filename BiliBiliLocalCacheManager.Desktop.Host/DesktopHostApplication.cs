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

internal sealed partial class DesktopHostApplication
{
    internal const int ProtocolVersion = 3;
    private const int DefaultPageSize = 100;
    private const int MaximumPageSize = 200;
    private const int MaximumIndexTokenLength = 128;
    private const int MaximumWireTextLength = 4096;
    private const long MaximumWireBytes = 9_007_199_254_740_991;
    private const int MaximumSessionProtectedArtifactCount = 64;
    private static readonly TimeSpan MaximumSessionProtectionAge = TimeSpan.FromHours(6);

    private static readonly string[] SupportedMethods =
    [
        "health",
        "initialState",
        "settings.get",
        "settings.update",
        "scan",
        "scan.issueLocation",
        "cancel",
        "search",
        "cache.details",
        "storage.get",
        "artifacts.cleanup",
        "artifacts.clear",
        "trash.move",
        "trash.list",
        "trash.page",
        "trash.restore",
        "trash.purge",
        "trash.purgeSnapshot",
        "play",
        "export",
        "diagnostics.export"
    ];

    private readonly CoreContracts.ICacheManager _cacheManager;
    private readonly CoreContracts.ICacheTrashService _trashService;
    private readonly PlaybackContracts.IPlaybackArtifactStore _artifactStore;
    private readonly CachePlaybackService _playbackService;
    private readonly PlaybackContracts.IPlaybackLauncher _desktopPlaybackLauncher;
    private readonly PlaybackContracts.IFfmpegDiagnosticsProvider _ffmpegDiagnosticsProvider =
        new BundledFfmpegDiagnosticsProvider();
    private readonly PlaybackContracts.IFfmpegPrewarmService _ffmpegPrewarmService =
        new BundledFfmpegPrewarmService();
    private readonly SettingsStore _settingsStore;
    private readonly DiagnosticEventRecorder _eventRecorder = new();
    private readonly DiagnosticExporter _diagnosticExporter;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _indexBuildGate = new(1, 1);
    private long _indexGeneration;
    private readonly SemaphoreSlim _artifactMaintenanceGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly object _artifactProtectionSync = new();
    private readonly LinkedList<SessionProtectedArtifact> _sessionProtectedArtifacts = new();
    private int _initialBackgroundWorkStarted;
    private int _backgroundArtifactCleanupQueued;
    private CacheIndex? _currentIndex;
    private string? _currentIndexToken;
    private string? _currentRoot;
    private bool _currentIncludeIncomplete;
    private DateTimeOffset? _lastScanCompletedAtUtc;

    public DesktopHostApplication(
        PlaybackContracts.IPlaybackLauncher? playbackLauncher = null,
        CoreContracts.ICacheTrashService? trashService = null,
        CoreContracts.ICacheManager? cacheManager = null,
        PlaybackContracts.ICacheExportMaterializationService? exportService = null)
    {
        _cacheManager = cacheManager ?? new CacheManager();
        _desktopPlaybackLauncher = playbackLauncher ?? new SystemPlaybackLauncher();
        _trashService = trashService ?? new FileSystemCacheTrashService();
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
        _exportService = exportService;
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
                "scan.issueLocation" => GetScanIssueLocation(parameters),
                "search" => await SearchAsync(parameters, cancellationToken),
                "cache.details" => await GetCacheDetailsAsync(parameters, cancellationToken),
                "storage.get" => await GetStorageAsync(requestId, parameters, cancellationToken),
                "artifacts.cleanup" => await CleanupArtifactsAsync(requestId, cancellationToken),
                "artifacts.clear" => await ClearArtifactsAsync(requestId, parameters, cancellationToken),
                "trash.move" => await MoveToTrashAsync(requestId, parameters, cancellationToken),
                "trash.list" => await ListTrashAsync(parameters, cancellationToken),
                "trash.page" => await ListTrashPageAsync(parameters, cancellationToken),
                "trash.restore" => await RestoreTrashAsync(requestId, parameters, cancellationToken),
                "trash.purge" => await PurgeTrashAsync(parameters, cancellationToken),
                "trash.purgeSnapshot" => await PurgeTrashSnapshotAsync(parameters, cancellationToken),
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
            protocolVersion = ProtocolVersion,
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
            protocolVersion = ProtocolVersion,
            settings = ToWireSettings(settings.Settings),
            settingsState = ToWireSettingsState(settings),
            // A previous non-persisting validation scan can target a different
            // root than the saved settings. Never replay that process-local index
            // after a renderer reload without an explicit root binding.
            items = Array.Empty<CacheSummaryDto>(),
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
        lock (_stateSync)
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
        return entries.Select(entry =>
        {
            ValidateTrashWirePath(entry.TrashPath);
            ValidateTrashWirePath(entry.OriginalPath);
            if (entry.TotalBytes < 0 || entry.TotalBytes > MaximumWireBytes)
                throw new RpcException("trash_too_large", "A trash entry exceeds the desktop numeric safety limit.");
            return new TrashEntryDto(
                entry.TrashPath,
                entry.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"av{entry.Avid}",
                entry.TotalBytes,
                entry.DeletedAtUtc,
                entry.OriginalPath,
                entry.PageIndex);
        })
            .ToArray();
    }

    private static string ResolveBatchExportDirectory(string requestedOutputPath)
    {
        string parent;
        string baseName;
        if (Directory.Exists(requestedOutputPath))
        {
            parent = requestedOutputPath;
            baseName = "cache-export";
        }
        else
        {
            parent = Path.GetDirectoryName(requestedOutputPath) ??
                     throw new IOException("The export output path has no parent directory.");
            baseName = Path.GetFileNameWithoutExtension(requestedOutputPath);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "cache-export";
            }
        }

        var candidate = Path.Combine(parent, baseName);
        for (var suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(parent, $"{baseName} ({suffix})");
        }

        return candidate;
    }

    private static string CreateBatchExportStagingDirectory(string destination)
    {
        var parent = Path.GetDirectoryName(destination) ??
                     throw new IOException("The export output path has no parent directory.");
        var destinationName = Path.GetFileName(destination);
        while (true)
        {
            var stagingDirectory = Path.Combine(
                parent,
                $".{destinationName}.{Guid.NewGuid():N}.staging");
            try
            {
                Directory.CreateDirectory(stagingDirectory);
                return stagingDirectory;
            }
            catch (IOException) when (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
            {
            }
        }
    }

    private static async Task CopyAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken,
        Action<long>? reportProgress = null)
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
                var buffer = new byte[128 * 1024];
                long copied = 0;
                var nextReport = Environment.TickCount64;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    if (Environment.TickCount64 >= nextReport)
                    {
                        reportProgress?.Invoke(copied);
                        nextReport = Environment.TickCount64 + 250;
                    }
                }
                reportProgress?.Invoke(copied);
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
            cacheDetails = true,
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


    private void ClearCurrentIndex()
    {
        lock (_stateSync)
        {
            _indexGeneration++;
            _indexSnapshot?.Invalidate();
            _indexSnapshot = null;
            _currentIndex = null;
            _currentIndexToken = null;
            _currentRoot = null;
            _lastScanCompletedAtUtc = null;
        }
    }

    private void ReportProgress(HostProgressEvent progress)
    {
        ProgressReported?.Invoke(this, progress);
    }

    private Action CreateDiskActivity(string requestId, string operation, CancellationToken cancellationToken)
    {
        var count = 0;
        var nextReport = 0L;
        return () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (Environment.TickCount64 < nextReport) return;
            ReportProgress(new HostProgressEvent(requestId, operation, "measuring", Current: count, Phase: "measure"));
            nextReport = Environment.TickCount64 + 250;
        };
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record SelectionTargetRequest(long Avid, IReadOnlyList<int>? PageIndexes);

    private sealed record PaginationRequest(int Offset, int PageSize);


    private sealed record SessionProtectedArtifact(string Path, DateTimeOffset ProtectedAtUtc);

    private sealed record ExportTargetRequest(long Avid, string? SegmentKey);
}
