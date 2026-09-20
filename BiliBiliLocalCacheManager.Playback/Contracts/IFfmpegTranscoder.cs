using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

public interface IFfmpegTranscoder
{
    Task ConcatToMp4Async(IReadOnlyList<string> inputFiles, string outputPath, TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => ConcatToMp4(inputFiles, outputPath, expectedDuration, progress, cancellationToken), cancellationToken);

    Task MuxDashPairToMp4Async(string videoPath, string audioPath, string outputPath, TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => MuxDashPairToMp4(videoPath, audioPath, outputPath, expectedDuration, progress, cancellationToken), cancellationToken);

    void ConcatToMp4(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken);

    void MuxDashPairToMp4(
        string videoPath,
        string audioPath,
        string outputPath,
        TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken);
}
