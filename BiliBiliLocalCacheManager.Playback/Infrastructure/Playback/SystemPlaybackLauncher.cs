using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class SystemPlaybackLauncher : IPlaybackLauncher
{
    public PlaybackLaunchResult Launch(PlaybackMaterializationResult materializationResult, PlaybackLaunchOptions? launchOptions = null)
    {
        ArgumentNullException.ThrowIfNull(materializationResult);

        if (!materializationResult.Succeeded || string.IsNullOrWhiteSpace(materializationResult.OutputPath))
        {
            return PlaybackLaunchResult.Failure(materializationResult.Message);
        }

        var effectiveOptions = launchOptions ?? new PlaybackLaunchOptions();
        foreach (var candidate in GetLaunchCandidates(effectiveOptions.PreferredPlayer))
        {
            if (candidate == PlayerKind.SystemDefault)
            {
                var shellResult = LaunchWithShell(materializationResult.OutputPath);
                if (shellResult.Succeeded ||
                    effectiveOptions.PreferredPlayer == PlaybackPlayerPreference.SystemDefaultOnly)
                {
                    return shellResult;
                }

                continue;
            }

            var player = DiscoverPlayer(candidate);
            if (player is null)
            {
                if (effectiveOptions.PreferredPlayer == PlaybackPlayerPreference.Mpv ||
                    effectiveOptions.PreferredPlayer == PlaybackPlayerPreference.Vlc)
                {
                    return PlaybackLaunchResult.Failure($"未找到指定播放器：{candidate}", candidate.ToString());
                }

                continue;
            }

            return LaunchWithKnownPlayer(player.Value, materializationResult.OutputPath);
        }

        return PlaybackLaunchResult.Failure("未找到可用播放器。当前策略为系统默认优先，其次 mpv、VLC。");
    }

    private static PlaybackLaunchResult LaunchWithKnownPlayer(DiscoveredPlayer player, string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = player.Path,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(filePath);
            Process.Start(startInfo);

            return PlaybackLaunchResult.Success(
                $"已使用 {player.DisplayName} 启动播放：{Path.GetFileName(filePath)}",
                player.DisplayName);
        }
        catch (Exception ex)
        {
            return PlaybackLaunchResult.Failure($"启动播放器失败：{ex.Message}", player.DisplayName);
        }
    }

    private static PlaybackLaunchResult LaunchWithShell(string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            };

            Process.Start(startInfo);
            return PlaybackLaunchResult.Success($"已尝试使用系统默认程序打开：{filePath}", "SystemDefault");
        }
        catch (Exception ex)
        {
            return PlaybackLaunchResult.Failure($"无法使用系统默认程序打开文件：{ex.Message}", "SystemDefault");
        }
    }

    private static IEnumerable<PlayerKind> GetLaunchCandidates(PlaybackPlayerPreference preferredPlayer)
    {
        switch (preferredPlayer)
        {
            case PlaybackPlayerPreference.SystemDefaultFirst:
                yield return PlayerKind.SystemDefault;
                yield return PlayerKind.Mpv;
                yield return PlayerKind.Vlc;
                yield break;
            case PlaybackPlayerPreference.SystemDefaultOnly:
                yield return PlayerKind.SystemDefault;
                yield break;
            case PlaybackPlayerPreference.Mpv:
                yield return PlayerKind.Mpv;
                yield break;
            case PlaybackPlayerPreference.Vlc:
                yield return PlayerKind.Vlc;
                yield break;
            default:
                throw new ArgumentOutOfRangeException(nameof(preferredPlayer), preferredPlayer, "Unknown player preference.");
        }
    }

    private static DiscoveredPlayer? DiscoverPlayer(PlayerKind kind)
    {
        var candidates = new[]
        {
            new DiscoveredPlayer(PlayerKind.Mpv, "mpv", FindExecutable("mpv.exe", new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mpv", "mpv.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "mpv", "mpv.exe")
            })),
            new DiscoveredPlayer(PlayerKind.Vlc, "VLC", FindExecutable("vlc.exe", new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe")
            }))
        };

        foreach (var candidate in candidates)
        {
            if (candidate.Kind == kind && !string.IsNullOrWhiteSpace(candidate.Path))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindExecutable(string executableName, IEnumerable<string> fallbackPaths)
    {
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore invalid PATH entries.
                }
            }
        }

        foreach (var fallbackPath in fallbackPaths)
        {
            if (File.Exists(fallbackPath))
            {
                return fallbackPath;
            }
        }

        return null;
    }

    private enum PlayerKind
    {
        SystemDefault,
        Mpv,
        Vlc
    }

    private readonly record struct DiscoveredPlayer(PlayerKind Kind, string DisplayName, string? Path);
}
