using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class SystemPlaybackLauncherTests
{
    [Theory]
    [InlineData(PlaybackPlayerPreference.Mpv, "mpv")]
    [InlineData(PlaybackPlayerPreference.Vlc, "VLC")]
    public void KnownPlayer_PassesSpecialPathAsOneArgument(PlaybackPlayerPreference preference, string expectedPlayer)
    {
        var launches = new List<ProcessStartInfo>();
        var launcher = new SystemPlaybackLauncher(launches.Add, (name, _) => name);
        var path = Path.Combine(Path.GetTempPath(), "\u4e2d\u6587 ' & file.m3u8");
        var result = launcher.Launch(PlaybackMaterializationResult.Success(path, true, "Ready", null), new() { PreferredPlayer = preference });
        Assert.True(result.Succeeded);
        Assert.Equal(expectedPlayer, result.PlayerName);
        var launch = Assert.Single(launches);
        Assert.False(launch.UseShellExecute);
        Assert.Equal(path, Assert.Single(launch.ArgumentList));
    }

    [Fact]
    public void ExplicitPlayerMissing_DoesNotFallBackToAnotherPlayer()
    {
        var launcher = new SystemPlaybackLauncher(_ => throw new Xunit.Sdk.XunitException("Must not launch"), (_, _) => null);
        Assert.False(launcher.Launch(PlaybackMaterializationResult.Success("local.mp4", true, "Ready", null),
            new() { PreferredPlayer = PlaybackPlayerPreference.Mpv }).Succeeded);
    }

    [Fact]
    public void SystemDefaultFailure_DoesNotStartAnotherPlayer()
    {
        var launches = new List<ProcessStartInfo>();
        var launcher = new SystemPlaybackLauncher(info =>
        {
            launches.Add(info);
            if (launches.Count == 1) throw new System.ComponentModel.Win32Exception("No association");
        }, (name, _) => name);
        var result = launcher.Launch(PlaybackMaterializationResult.Success("local.mp4", true, "Ready", null));
        Assert.False(result.Succeeded);
        Assert.Equal("SystemDefault", result.PlayerName);
        Assert.Single(launches);
    }

    [Fact]
    public void FailedMaterialization_DoesNotStartAProcess()
    {
        var launcher = new SystemPlaybackLauncher(_ => throw new Xunit.Sdk.XunitException("Must not launch"), (_, _) => null);
        Assert.False(launcher.Launch(PlaybackMaterializationResult.Failure("Failed")).Succeeded);
    }
}
