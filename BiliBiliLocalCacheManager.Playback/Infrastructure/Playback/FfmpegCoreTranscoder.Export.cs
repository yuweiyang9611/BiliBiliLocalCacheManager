using System.Buffers.Binary;
using BiliBiliLocalCacheManager.Playback.Models;
using FFMpegCore;

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
            var openEndedMedia = ValidateMp4Boxes(source, cancellationToken);
            await ValidateMp4StreamsAsync(source.Name, openEndedMedia, progress, cancellationToken).ConfigureAwait(false);
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

    private static async Task ValidateMp4StreamsAsync(string path, bool openEndedMedia, IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(async (reporter, token) =>
            {
                var analysis = await FFProbe.AnalyseAsync(path, cancellationToken: token).ConfigureAwait(false);
                if (!analysis.Format.FormatName.Split(',').Contains("mp4", StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("The file is not an MP4 container.");
                if (analysis.PrimaryVideoStream is null && analysis.PrimaryAudioStream is null)
                    throw new InvalidDataException("The MP4 contains no readable audio or video stream.");
                if (openEndedMedia)
                    await VerifyOpenEndedMediaPacketsAsync(path, reporter, token).ConfigureAwait(false);
                reporter.Report(new PlaybackPreparationProgress("MP4 streams verified", 100, TimeSpan.Zero, null, Phase: "probe"));
            }, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not InvalidDataException)
        {
            throw new IOException("FFprobe could not verify the MP4: " + exception.Message, exception);
        }
    }

    // ffprobe can still identify streams from an intact moov in a file with a truncated mdat.
    private static bool ValidateMp4Boxes(FileStream source, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[8];
        var foundMovie = false;
        var foundMedia = false;
        var openEndedMedia = false;
        while (source.Position < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = source.Position;
            source.ReadExactly(header);
            var movie = header[4..].SequenceEqual("moov"u8);
            var media = header[4..].SequenceEqual("mdat"u8);
            var remaining = (ulong)(source.Length - start);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size == 1)
            {
                source.ReadExactly(header);
                size = BinaryPrimitives.ReadUInt64BigEndian(header);
            }
            else if (size == 0)
            {
                openEndedMedia = media;
                size = remaining;
            }
            var headerSize = (ulong)(source.Position - start);
            if (size < headerSize || size > remaining)
                throw new InvalidDataException("The MP4 contains a truncated or invalid box.");
            foundMovie |= movie && size > headerSize;
            foundMedia |= media && size > headerSize;
            source.Position = start + (long)size;
        }
        if (!foundMovie || !foundMedia)
            throw new InvalidDataException("The MP4 is missing movie metadata or media data.");
        source.Position = 0;
        return openEndedMedia;
    }
}
