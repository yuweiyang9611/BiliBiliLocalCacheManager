using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class FfmpegCoreTranscoder
{
    public async Task ProcessAsync(CachePlaybackPlan plan, string outputPath, Action<string> requireAudioTranscode,
        IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requireAudioTranscode);
        if (plan.MaterialKind == CachePlaybackMaterialKind.SingleFile &&
            string.Equals(Path.GetExtension(plan.MediaFiles[0]), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            await using var source = new FileStream(plan.MediaFiles[0], FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous);
            var buffer = new byte[1024 * 1024];
            long bytes = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                bytes += count;
                progress?.Report(new PlaybackPreparationProgress("Preparing export copy", null, TimeSpan.Zero, null,
                    Phase: "export-copy", ProcessedBytes: bytes));
            }
            return;
        }
        if (plan.MaterialKind == CachePlaybackMaterialKind.DashPair)
        {
            await RunAsync((reporter, token) => MuxDashPairCoreAsync(plan.MediaFiles[0], plan.MediaFiles[1], outputPath,
                plan.Duration, reporter, token, requireAudioTranscode), progress, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (plan.MaterialKind is CachePlaybackMaterialKind.SingleFile or CachePlaybackMaterialKind.OrderedPair)
        {
            await ConcatToMp4Async(plan.MediaFiles, outputPath, plan.Duration, progress, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new IOException("This media layout cannot be exported reliably.");
    }
}
