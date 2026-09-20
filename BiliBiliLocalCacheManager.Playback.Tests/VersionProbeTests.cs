using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class VersionProbeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("blcm-version-probe-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionProbe_TimesOutEvenWhenProcessKeepsItsPipesOpen(bool writesVersion)
    {
        // Leave stdin open so the shell blocks without a sleep process or busy loop.
        var start = CreateStartInfo(
            (writesVersion ? "echo test-version& " : "") + "set /p ignored=",
            (writesVersion ? "printf 'test-version\n'; " : "") + "read ignored");
        var result = await BundledFfmpegBootstrapper.ReadProcessVersionAsync(start, TimeSpan.FromMilliseconds(200))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VersionProbe_DrainsStderrAndTrailingStdout(bool stderrBeforeVersion)
    {
        // A single long line exceeds pipe buffers without relying on interpreter startup.
        await File.WriteAllTextAsync(Path.Combine(_root, "probe-output.txt"), new string('x', 1024 * 1024));
        var windows = stderrBeforeVersion
            ? "type probe-output.txt 1>&2 & echo test-version& type probe-output.txt"
            : "echo test-version& type probe-output.txt 1>&2 & type probe-output.txt";
        var unix = stderrBeforeVersion
            ? "cat probe-output.txt >&2; printf 'test-version\n'; cat probe-output.txt"
            : "printf 'test-version\n'; cat probe-output.txt >&2; cat probe-output.txt";
        Assert.Equal("test-version", await BundledFfmpegBootstrapper.ReadProcessVersionAsync(
            CreateStartInfo(windows, unix), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task VersionProbe_ExitedProcessWithoutOutputReturnsNull()
    {
        Assert.Null(await BundledFfmpegBootstrapper.ReadProcessVersionAsync(
            CreateStartInfo("exit /b 0", "exit 0"), TimeSpan.FromSeconds(10)));
    }

    private ProcessStartInfo CreateStartInfo(string windowsCommand, string unixCommand) => new()
    {
        // Keep pipe-drain checks independent of PowerShell cold start on Windows CI.
        FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
        Arguments = OperatingSystem.IsWindows() ? $"/d /q /c \"{windowsCommand}\"" : $"-c \"{unixCommand}\"",
        WorkingDirectory = _root,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
