using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Wpf.Models;

public sealed class DiagnosticReportContext
{
    public string ProductName { get; init; } = string.Empty;

    public string InformationalVersion { get; init; } = string.Empty;

    public TimeSpan Uptime { get; init; }

    public string OperatingSystem { get; init; } = string.Empty;

    public string RuntimeVersion { get; init; } = string.Empty;

    public string OperatingSystemArchitecture { get; init; } = string.Empty;

    public string ProcessArchitecture { get; init; } = string.Empty;

    public string Culture { get; init; } = string.Empty;

    public int SettingsSchemaVersion { get; init; }

    public AppSettingsLoadKind? SettingsLoadKind { get; init; }

    public int? SourceSettingsSchemaVersion { get; init; }

    public bool SettingsSaveEnabled { get; init; }

    public bool AutomaticTranscodeCacheMaintenanceEnabled { get; init; }

    public string PreferredPlayer { get; init; } = string.Empty;

    public bool IncludeIncompleteCache { get; init; }

    public int TranscodeCacheRetentionDays { get; init; }

    public int TranscodeCacheMaxSizeGigabytes { get; init; }

    public string? CacheRoot { get; init; }

    public StorageOverviewSnapshot? StorageOverview { get; init; }

    public string? LastStorageMaintenance { get; init; }

    public FfmpegDiagnosticSnapshot Ffmpeg { get; init; } =
        FfmpegDiagnosticSnapshot.NotInitialized;

    public string? LastPlaybackFailure { get; init; }
}
