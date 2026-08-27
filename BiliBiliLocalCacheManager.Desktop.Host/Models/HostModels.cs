using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed record HostProgressEvent(
    string RequestId,
    string Operation,
    string Stage,
    double? Percentage = null,
    int? Current = null,
    int? Total = null,
    string? Message = null,
    object? Details = null);

internal sealed class DesktopSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string RootPath { get; set; } = string.Empty;

    public bool IncludeIncomplete { get; set; }

    public string Keyword { get; set; } = string.Empty;

    public bool SplitKeywords { get; set; } = true;

    public bool AnyKeywords { get; set; }

    public bool IncludePartName { get; set; } = true;

    public bool IncludeOwnerName { get; set; } = true;

    public bool IncludeBvid { get; set; } = true;

    public bool IncludeAvid { get; set; } = true;

    public bool CaseSensitive { get; set; }

    public CacheSearchMatchMode MatchMode { get; set; } = CacheSearchMatchMode.Contains;

    public PlaybackPlayerPreference PreferredPlayer { get; set; } =
        PlaybackPlayerPreference.SystemDefaultFirst;

    public int TranscodeCacheRetentionDays { get; set; } =
        PlaybackArtifactCleanupOptions.DefaultRetentionDays;

    public int TranscodeCacheMaxSizeGigabytes { get; set; } =
        10;

    public DesktopSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        RootPath = RootPath,
        IncludeIncomplete = IncludeIncomplete,
        Keyword = Keyword,
        SplitKeywords = SplitKeywords,
        AnyKeywords = AnyKeywords,
        IncludePartName = IncludePartName,
        IncludeOwnerName = IncludeOwnerName,
        IncludeBvid = IncludeBvid,
        IncludeAvid = IncludeAvid,
        CaseSensitive = CaseSensitive,
        MatchMode = MatchMode,
        PreferredPlayer = PreferredPlayer,
        TranscodeCacheRetentionDays = TranscodeCacheRetentionDays,
        TranscodeCacheMaxSizeGigabytes = TranscodeCacheMaxSizeGigabytes
    };

    public PlaybackArtifactCleanupOptions CreateCleanupOptions() =>
        PlaybackArtifactCleanupOptions.FromUserLimits(
            TranscodeCacheRetentionDays,
            TranscodeCacheMaxSizeGigabytes);
}

internal sealed record SettingsState(
    DesktopSettings Settings,
    bool CanSave,
    int? SourceSchemaVersion,
    string? Message);

internal sealed record CacheDto(
    string Id,
    string Avid,
    string Title,
    string Bvid,
    string OwnerName,
    double DurationSeconds,
    int SegmentCount,
    long SizeBytes,
    bool IsAllCompleted,
    DateTimeOffset? LastUpdated,
    IReadOnlyList<SegmentDto> Segments);

internal sealed record SegmentDto(
    string Id,
    string SegmentKey,
    int PageIndex,
    string PartName,
    string StructureKind,
    string MaterialKind,
    long SizeBytes,
    double DurationSeconds,
    bool IsPlayable,
    string? DirectoryPath);

internal sealed record TrashEntryDto(
    string Id,
    string Avid,
    string Title,
    long SizeBytes,
    DateTimeOffset? DeletedAt,
    string? OriginalPath);

internal sealed record DiagnosticEvent(
    DateTimeOffset TimestampUtc,
    string Category,
    string Level,
    string Message,
    string? ExceptionType = null);
