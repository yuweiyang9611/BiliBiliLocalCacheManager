using BiliBiliLocalCacheManager.Playback.Models;
using FFMpegCore;
using FFMpegCore.Enums;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class FfmpegCoreTranscoder
{
    private static async Task MuxDashAudioFallbackAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        TimeSpan duration,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryDeleteFile(outputPath);

        const string fallbackStage = "\u97f3\u9891\u9700\u8981\u517c\u5bb9\u8f6c\u7801\uff0c\u6b63\u5728\u5408\u5e76 DASH \u97f3\u89c6\u9891";
        var tracker = new ProgressTracker(progress);
        tracker.Report(fallbackStage, 0d);

        var processor = FFMpegArguments
            .FromFileInput(videoPath)
            .AddFileInput(audioPath)
            .OutputToFile(outputPath, true, options => options
                .WithCustomArgument("-map 0:v:0 -map 1:a:0")
                .CopyChannel(Channel.Video)
                .WithAudioCodec(AudioCodec.Aac)
                .WithAudioBitrate(AudioQuality.Good)
                .UsingShortest(false));

        if (duration > TimeSpan.Zero)
        {
            processor.NotifyOnProgress(
                percentage => tracker.Report(fallbackStage, percentage),
                duration);
        }

        var succeeded = await processor
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously()
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!succeeded)
        {
            throw new InvalidOperationException(
                "FFmpeg failed while muxing DASH media with AAC audio fallback.");
        }
    }

    private static void TryDeleteFile(string path)
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
            // The retry will surface a useful FFmpeg error if the partial file is still locked.
        }
    }
}
