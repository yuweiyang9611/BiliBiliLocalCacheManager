namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed record PlaybackPreparationProgress(
    string Stage,
    double? Percentage,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining);
