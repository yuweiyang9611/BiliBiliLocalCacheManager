using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class VersionProbeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("blcm-version-probe-").FullName;

    [Fact]
    public async Task VersionProbe_TimesOutAndReleasesItsDirectoryWithNoAvailablePoolWorkers()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "probe")).FullName;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(VersionProbeTests).Assembly.Location);
        start.ArgumentList.Add("--starved-version-probe");
        start.ArgumentList.Add(directory);
        using var helper = Process.Start(start)!;
        using var stderr = helper.StandardError;
        var errors = stderr.ReadToEndAsync();
        try
        {
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(helper.ExitCode == 0, await errors);
            Assert.False(Directory.Exists(directory), "The helper did not finish its directory cleanup check.");
        }
        finally
        {
            if (!helper.HasExited)
            {
                try { helper.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (helper.HasExited) { }
                catch (System.ComponentModel.Win32Exception) when (helper.HasExited) { }
                await helper.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task VersionProbe_OnLinuxTimesOutWhenDescendantKeepsPipesOpenAfterShellExits()
    {
        if (!OperatingSystem.IsLinux()) return;

        // The shell exits immediately. Its background child inherits stdout/stderr until we release it.
        const string command = """
            (
                : > holding
                i=0
                while [ ! -f release ] && [ $i -lt 200 ]; do sleep 0.1; i=$((i+1)); done
                : > released
            ) &
            printf 'test-version\n'
            """;
        var start = CreateStartInfo(_root, "", command);
        var holding = Path.Combine(_root, "holding");
        var released = Path.Combine(_root, "released");
        try
        {
            var version = await BundledFfmpegBootstrapper.ReadProcessVersionAsync(start, TimeSpan.FromSeconds(1))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(version);
            Assert.True(File.Exists(holding), "The descendant did not start.");
            Assert.False(File.Exists(released), "The probe waited for the descendant to close the pipes.");
        }
        finally
        {
            File.WriteAllText(Path.Combine(_root, "release"), "");
            if (File.Exists(holding))
            {
                Assert.True(SpinWait.SpinUntil(() => File.Exists(released), TimeSpan.FromSeconds(5)),
                    "The descendant did not acknowledge release.");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionProbe_TimesOutEvenWhenProcessKeepsItsPipesOpen(bool writesVersion)
    {
        var start = CreateBlockingShell(_root, writesVersion);
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
            CreateStartInfo(_root, windows, unix), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task VersionProbe_ExitedProcessWithoutOutputReturnsNull()
    {
        Assert.Null(await BundledFfmpegBootstrapper.ReadProcessVersionAsync(
            CreateStartInfo(_root, "exit /b 0", "exit 0"), TimeSpan.FromSeconds(10)));
    }

    // Leave stdin open so the shell blocks without a sleep process or busy loop.
    internal static ProcessStartInfo CreateBlockingShell(string directory, bool writesVersion) =>
        CreateStartInfo(directory, (writesVersion ? "echo test-version& " : "") + "set /p ignored=",
            (writesVersion ? "printf 'test-version\n'; " : "") + "read ignored");

    private static ProcessStartInfo CreateStartInfo(string directory, string windowsCommand, string unixCommand)
    {
        // Keep pipe-drain checks independent of PowerShell cold start on Windows CI.
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
            start.Arguments = $"/d /q /c \"{windowsCommand}\"";
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(unixCommand.ReplaceLineEndings("\n"));
        }
        return start;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
