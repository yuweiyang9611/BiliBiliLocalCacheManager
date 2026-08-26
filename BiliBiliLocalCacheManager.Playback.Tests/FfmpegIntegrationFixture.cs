using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using FFMpegCore;

namespace BiliBiliLocalCacheManager.Playback.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FfmpegIntegrationCollection :
    ICollectionFixture<FfmpegIntegrationFixture>
{
    public const string Name = "Real FFmpeg integration";
}

public sealed class FfmpegIntegrationFixture : IDisposable
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(1);

    public FfmpegIntegrationFixture()
    {
        if (!FfmpegIntegrationFactAttribute.IsEnabled)
        {
            return;
        }

        RootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"bili_ffmpeg_integration_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootDirectory);

        using var bootstrapCancellation =
            new CancellationTokenSource(TimeSpan.FromMinutes(10));
        BundledFfmpegBootstrapper.EnsureConfigured(bootstrapCancellation.Token);

        var binaryFolder = GlobalFFOptions.Current.BinaryFolder;
        FfmpegPath = Path.Combine(
            binaryFolder,
            BundledFfmpegBootstrapper.FfmpegExecutableName);
        FfprobePath = Path.Combine(
            binaryFolder,
            BundledFfmpegBootstrapper.FfprobeExecutableName);
        if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
        {
            throw new FileNotFoundException(
                $"FFmpeg integration binaries were not found in {binaryFolder}.");
        }

        VideoPath = Path.Combine(RootDirectory, "video.m4s");
        AacAudioPath = Path.Combine(RootDirectory, "audio-aac.m4s");
        WmaAudioPath = Path.Combine(RootDirectory, "audio-wma.wma");
        CancellationAudioPath = Path.Combine(RootDirectory, "cancel.flac");

        RunFfmpeg(
            "-f", "lavfi",
            "-i", "color=c=blue:s=160x90:r=10:d=2",
            "-an",
            "-c:v", "mpeg4",
            "-q:v", "5",
            "-movflags", "+frag_keyframe+empty_moov",
            "-f", "mp4",
            VideoPath);
        RunFfmpeg(
            "-f", "lavfi",
            "-i", "sine=frequency=1000:sample_rate=48000:duration=2",
            "-vn",
            "-c:a", "aac",
            "-b:a", "96k",
            "-movflags", "+frag_keyframe+empty_moov",
            "-f", "mp4",
            AacAudioPath);
        RunFfmpeg(
            "-f", "lavfi",
            "-i", "sine=frequency=750:sample_rate=48000:duration=2",
            "-vn",
            "-c:a", "wmav2",
            "-b:a", "96k",
            "-f", "asf",
            WmaAudioPath);

        // One hour of compressed silence is only a few megabytes, but keeps the
        // AAC fallback active long enough to exercise cancellation mid-process.
        RunFfmpeg(
            "-f", "lavfi",
            "-i", "anullsrc=r=48000:cl=stereo",
            "-t", "3600",
            "-vn",
            "-c:a", "flac",
            CancellationAudioPath);
    }

    public string RootDirectory { get; } = string.Empty;

    public string FfmpegPath { get; } = string.Empty;

    public string FfprobePath { get; } = string.Empty;

    public string VideoPath { get; } = string.Empty;

    public string AacAudioPath { get; } = string.Empty;

    public string WmaAudioPath { get; } = string.Empty;

    public string CancellationAudioPath { get; } = string.Empty;

    public FfmpegIntegrationWorkspace CreateWorkspace(
        bool includeCancellationAudio = false)
    {
        return new FfmpegIntegrationWorkspace(this, includeCancellationAudio);
    }

    public string GetCodecName(string mediaPath, string streamSpecifier)
    {
        return RunFfprobe(
                "-select_streams", streamSpecifier,
                "-show_entries", "stream=codec_name",
                "-of", "default=noprint_wrappers=1:nokey=1",
                mediaPath)
            .Trim();
    }

    public IReadOnlyList<string> GetPacketHashes(
        string mediaPath,
        string streamSpecifier)
    {
        return RunFfprobe(
                "-select_streams", streamSpecifier,
                "-show_packets",
                "-show_entries", "packet=data_hash",
                "-show_data_hash", "sha256",
                "-of", "csv=p=0",
                mediaPath)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.StartsWith("SHA256:", StringComparison.Ordinal))
            .Select(value => value.Split(',', 2)[0])
            .ToArray();
    }

    public void Dispose()
    {
        TryDeleteDirectory(RootDirectory);
    }

    private void RunFfmpeg(params string[] arguments)
    {
        RunTool(
            FfmpegPath,
            new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }
                .Concat(arguments),
            ToolTimeout);
    }

    private string RunFfprobe(params string[] arguments)
    {
        return RunTool(
                FfprobePath,
                new[] { "-v", "error" }.Concat(arguments),
                ToolTimeout)
            .StandardOutput;
    }

    private static ToolResult RunTool(
        string executablePath,
        IEnumerable<string> arguments,
        TimeSpan timeout)
    {
        var argumentList = arguments.ToArray();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in argumentList)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Failed to start integration tool {executablePath}.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(
                $"Integration tool timed out after {timeout}: {executablePath}");
        }

        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Integration tool exited with code {process.ExitCode}: " +
                $"{executablePath} {string.Join(' ', argumentList)}{Environment.NewLine}{error}");
        }

        return new ToolResult(output, error);
    }

    internal static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup only.
        }
    }

    private sealed record ToolResult(string StandardOutput, string StandardError);
}

public sealed class FfmpegIntegrationWorkspace : IDisposable
{
    public FfmpegIntegrationWorkspace(
        FfmpegIntegrationFixture fixture,
        bool includeCancellationAudio)
    {
        RootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"bili_ffmpeg_integration_case_{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootDirectory);

        VideoPath = CopyToWorkspace(fixture.VideoPath);
        AacAudioPath = CopyToWorkspace(fixture.AacAudioPath);
        WmaAudioPath = CopyToWorkspace(fixture.WmaAudioPath);
        if (includeCancellationAudio)
        {
            CancellationAudioPath = CopyToWorkspace(fixture.CancellationAudioPath);
        }
    }

    public string RootDirectory { get; }

    public string VideoPath { get; }

    public string AacAudioPath { get; }

    public string WmaAudioPath { get; }

    public string? CancellationAudioPath { get; }

    public string ArtifactRoot => Path.Combine(RootDirectory, "artifacts");

    public void Dispose()
    {
        FfmpegIntegrationFixture.TryDeleteDirectory(RootDirectory);
    }

    private string CopyToWorkspace(string sourcePath)
    {
        var destinationPath = Path.Combine(
            RootDirectory,
            Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destinationPath);
        return destinationPath;
    }
}
