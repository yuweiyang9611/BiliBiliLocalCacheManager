using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using FFMpegCore;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class FfmpegCoreTranscoder : IFfmpegTranscoder
{
    public void ConcatToMp4(IReadOnlyList<string> inputFiles, string outputPath)
    {
        ConcatToMp4(
            inputFiles,
            outputPath,
            TimeSpan.Zero,
            progress: null,
            CancellationToken.None);
    }

    public void ConcatToMp4(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (inputFiles.Count == 0)
        {
            throw new ArgumentException("At least one input file is required.", nameof(inputFiles));
        }

        ConcatToMp4Async(
                inputFiles,
                outputPath,
                expectedDuration,
                progress,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    public void MuxDashPairToMp4(string videoPath, string audioPath, string outputPath)
    {
        MuxDashPairToMp4(
            videoPath,
            audioPath,
            outputPath,
            TimeSpan.Zero,
            progress: null,
            CancellationToken.None);
    }

    public void MuxDashPairToMp4(
        string videoPath,
        string audioPath,
        string outputPath,
        TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        MuxDashPairToMp4Async(
                videoPath,
                audioPath,
                outputPath,
                expectedDuration,
                progress,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task ConcatToMp4Async(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tracker = new ProgressTracker(progress);
        tracker.Report("\u6b63\u5728\u51c6\u5907 FFmpeg", percentage: null);

        BundledFfmpegBootstrapper.EnsureConfigured(
            cancellationToken,
            tracker.Report);
        cancellationToken.ThrowIfCancellationRequested();

        var temporaryRoot = Path.Combine(
            GlobalFFOptions.Current.TemporaryFilesFolder,
            $"join-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            var manifestLines = new List<string>(inputFiles.Count + 1)
            {
                "ffconcat version 1.0"
            };
            var analysedDuration = TimeSpan.Zero;
            var allDurationsKnown = true;

            for (var index = 0; index < inputFiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inputPath = inputFiles[index];
                if (expectedDuration <= TimeSpan.Zero)
                {
                    tracker.Report(
                        $"\u6b63\u5728\u5206\u6790\u5a92\u4f53\u5206\u7247 {index + 1}/{inputFiles.Count}",
                        percentage: null);

                    var analysis = await FFProbe.AnalyseAsync(
                            inputPath,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (analysis.Duration > TimeSpan.Zero)
                    {
                        analysedDuration += analysis.Duration;
                    }
                    else
                    {
                        allDurationsKnown = false;
                    }
                }

                manifestLines.Add(FormatConcatFileEntry(inputPath));
            }

            var manifestPath = Path.Combine(temporaryRoot, "inputs.ffconcat");
            await File.WriteAllLinesAsync(
                    manifestPath,
                    manifestLines,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);

            const string concatStage = "\u6b63\u5728\u5408\u5e76\u5a92\u4f53\u5206\u7247";
            tracker.Report(concatStage, 0d);
            var totalDuration = !allDurationsKnown && expectedDuration > TimeSpan.Zero
                ? expectedDuration
                : analysedDuration > TimeSpan.Zero
                    ? analysedDuration
                    : expectedDuration;

            var processor = FFMpegArguments
                .FromFileInput(manifestPath, true, options => options
                    .WithCustomArgument("-f concat")
                    .WithCustomArgument("-safe 0"))
                .OutputToFile(outputPath, true, options => options.CopyChannel());

            if (totalDuration > TimeSpan.Zero)
            {
                processor.NotifyOnProgress(
                    percentage => tracker.Report(concatStage, percentage),
                    totalDuration);
            }
            else
            {
                tracker.Report(concatStage, percentage: null);
            }

            var succeeded = await processor
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously()
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!succeeded)
            {
                throw new InvalidOperationException("FFmpeg failed while joining media parts.");
            }

            tracker.Report("\u8f6c\u7801\u5b8c\u6210", 100d);
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private static async Task MuxDashPairToMp4Async(
        string videoPath,
        string audioPath,
        string outputPath,
        TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tracker = new ProgressTracker(progress);
        tracker.Report("\u6b63\u5728\u51c6\u5907 FFmpeg", percentage: null);

        BundledFfmpegBootstrapper.EnsureConfigured(
            cancellationToken,
            tracker.Report);
        cancellationToken.ThrowIfCancellationRequested();

        tracker.Report(
            "\u6b63\u5728\u68c0\u67e5\u97f3\u9891\u517c\u5bb9\u6027",
            percentage: null);
        var audioAnalysis = await FFProbe.AnalyseAsync(
                audioPath,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var duration = expectedDuration > TimeSpan.Zero
            ? expectedDuration
            : audioAnalysis.Duration;
        if (duration <= TimeSpan.Zero)
        {
            tracker.Report("\u6b63\u5728\u5206\u6790\u97f3\u89c6\u9891", 0d);
            var videoAnalysis = await FFProbe.AnalyseAsync(
                    videoPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            duration = videoAnalysis.Duration;
        }

        if (!string.Equals(
                audioAnalysis.PrimaryAudioStream?.CodecName,
                "aac",
                StringComparison.OrdinalIgnoreCase))
        {
            await MuxDashAudioFallbackAsync(
                    videoPath,
                    audioPath,
                    outputPath,
                    duration,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            tracker.Report("\u8f6c\u7801\u5b8c\u6210", 100d);
            return;
        }

        const string muxStage = "\u6b63\u5728\u5408\u5e76 DASH \u97f3\u89c6\u9891";
        var processor = FFMpegArguments
            .FromFileInput(videoPath)
            .AddFileInput(audioPath)
            .OutputToFile(outputPath, true, options => options
                .CopyChannel()
                .WithCustomArgument("-map 0:v:0 -map 1:a:0")
                .UsingShortest(false));

        if (duration > TimeSpan.Zero)
        {
            processor.NotifyOnProgress(
                percentage => tracker.Report(muxStage, percentage),
                duration);
        }
        else
        {
            tracker.Report(muxStage, percentage: null);
        }

        try
        {
            var succeeded = await processor
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously()
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!succeeded)
            {
                throw new InvalidOperationException("FFmpeg failed while muxing DASH media.");
            }
        }
        catch (Exception ex) when (
            ex is FFMpegCore.Exceptions.FFMpegException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await MuxDashAudioFallbackAsync(
                    videoPath,
                    audioPath,
                    outputPath,
                    duration,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        tracker.Report("\u8f6c\u7801\u5b8c\u6210", 100d);
    }

    private static string FormatConcatFileEntry(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var normalizedPath = Path.GetFullPath(path).Replace('\\', '/');
        var escapedPath = normalizedPath.Replace("'", "'\\''", StringComparison.Ordinal);
        return $"file '{escapedPath}'";
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
            // Best-effort cleanup. The artifact store still removes its partial output.
        }
    }

    private sealed class ProgressTracker(IProgress<PlaybackPreparationProgress>? progress)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly Stopwatch _stageStopwatch = Stopwatch.StartNew();
        private readonly object _sync = new();
        private string? _stage;
        private double _lastPercentage;

        public void Report(string stage, double? percentage)
        {
            if (progress is null)
            {
                return;
            }

            lock (_sync)
            {
                if (!string.Equals(_stage, stage, StringComparison.Ordinal))
                {
                    _stage = stage;
                    _lastPercentage = 0d;
                    _stageStopwatch.Restart();
                }

                double? normalized = null;
                if (percentage.HasValue)
                {
                    normalized = Math.Max(_lastPercentage, Math.Clamp(percentage.Value, 0d, 100d));
                    _lastPercentage = normalized.Value;
                }

                var elapsed = _stopwatch.Elapsed;
                TimeSpan? remaining = null;
                if (normalized >= 100d)
                {
                    remaining = TimeSpan.Zero;
                }
                else if (normalized >= 0.5d)
                {
                    var seconds = _stageStopwatch.Elapsed.TotalSeconds *
                        (100d - normalized.Value) /
                        normalized.Value;
                    if (double.IsFinite(seconds) && seconds >= 0d && seconds <= TimeSpan.MaxValue.TotalSeconds)
                    {
                        remaining = TimeSpan.FromSeconds(seconds);
                    }
                }

                progress.Report(new PlaybackPreparationProgress(
                    stage,
                    normalized,
                    elapsed,
                    remaining));
            }
        }
    }
}
