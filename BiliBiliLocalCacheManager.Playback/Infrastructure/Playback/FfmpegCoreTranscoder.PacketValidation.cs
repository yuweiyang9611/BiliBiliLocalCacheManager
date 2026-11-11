using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Models;
using FFMpegCore;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class FfmpegCoreTranscoder
{
    // A zero-sized mdat extends to EOF. A short file can still have valid box sizes
    // and track headers. Compare the packets actually present against the sample counts.
    private static async Task VerifyOpenEndedMediaPacketsAsync(string path,
        IProgress<PlaybackPreparationProgress> progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Path.Combine(GlobalFFOptions.Current.BinaryFolder, BundledFfmpegBootstrapper.FfprobeExecutableName))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Ignore presentation edits when counting physical samples, including trimmed preroll.
        foreach (var argument in new[] { "-v", "error", "-ignore_editlist", "1", "-count_packets", "-show_packets",
                     "-show_entries", "packet=stream_index,duration,pos,flags:packet_side_data=:stream=index,codec_name,codec_type,duration_ts,nb_frames,nb_read_packets",
                     "-of", "csv=p=1", path })
            start.ArgumentList.Add(argument);

        using var probe = Process.Start(start) ?? throw new IOException("Could not start FFprobe.");
        string? error = null;
        probe.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrEmpty(line.Data))
                Interlocked.CompareExchange(ref error, line.Data[..Math.Min(line.Data.Length, 1024)], null);
        };
        probe.BeginErrorReadLine();
        var timer = Stopwatch.StartNew();
        var sawMediaPackets = false;
        var packetDurations = new Dictionary<int, long>();
        try
        {
            while (await probe.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var fields = line.Split(',');
                if (fields.Length >= 5 && fields[0] == "packet")
                {
                    if (fields[4].Contains('C')) throw new InvalidDataException("FFprobe found a corrupt MP4 packet.");
                    if (int.TryParse(fields[1], out var stream) && long.TryParse(fields[2], out var duration))
                        packetDurations[stream] = checked(packetDurations.GetValueOrDefault(stream) + duration);
                    if (timer.ElapsedMilliseconds >= 250 && long.TryParse(fields[3], out var position))
                    {
                        progress.Report(new PlaybackPreparationProgress("Verifying MP4 packets", null, TimeSpan.Zero, null,
                            Phase: "probe", ProcessedBytes: position));
                        timer.Restart();
                    }
                }
                else if (fields.Length >= 7 && fields[0] == "stream" && fields[3] is "video" or "audio")
                {
                    long.TryParse(fields[6], out var readPackets);
                    var hasSamples = long.TryParse(fields[5], out var declaredSamples) && declaredSamples > 0;
                    var hasDuration = long.TryParse(fields[4], out var declaredDuration) && declaredDuration > 0;
                    var coversDuration = hasDuration && int.TryParse(fields[1], out var stream)
                        && packetDurations.GetValueOrDefault(stream) >= declaredDuration;
                    if (hasSamples && readPackets < declaredSamples)
                    {
                        // Video and AAC have one packet per coded sample. Audio demuxers
                        // can group samples (e.g. PCM); then verify the entire track in its
                        // own timescale instead of accepting an unexplained sample deficit.
                        if (fields[3] == "video" || fields[2] == "aac" || !coversDuration)
                            throw new InvalidDataException("The MP4 ends before all declared media samples.");
                    }
                    else if (!hasSamples && hasDuration && fields[3] == "audio" && !coversDuration)
                        throw new InvalidDataException("The MP4 ends before the declared audio duration.");
                    sawMediaPackets |= readPackets > 0;
                }
            }
            await probe.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode != 0 || error is not null || !sawMediaPackets)
                throw new InvalidDataException("FFprobe could not read the MP4 packets. " + error);
        }
        finally
        {
            if (!probe.HasExited)
            {
                try { probe.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (probe.HasExited) { }
                await probe.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }
}
