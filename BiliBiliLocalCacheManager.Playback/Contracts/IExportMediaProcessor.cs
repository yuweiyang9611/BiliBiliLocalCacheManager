using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

/// <summary>The external media processor must invoke the authorization callback before re-encoding.</summary>
public interface IExportMediaProcessor
{
    Task ProcessAsync(CachePlaybackPlan plan, string outputPath, Action<string> requireAudioTranscode,
        IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken);
}

public interface ICacheExportMaterializationService
{
    Task<PlaybackMaterializationResult> MaterializeAsync(CachePlaybackPlan plan, IReadOnlyCollection<string> approvals,
        IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken);

    Task ValidatePreparedAsync(CachePlaybackPlan plan, PlaybackMaterializationResult materialization,
        IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken, string? stagedOutputPath = null);
}
