using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class SystemPlaybackLauncher : IPlaybackLauncher
{
    private readonly Action<ProcessStartInfo> _start;
    private readonly Func<string, IEnumerable<string>, string?> _findExecutable;

    public SystemPlaybackLauncher() : this(info => { using var process = Process.Start(info); }, FindExecutable) { }

    internal SystemPlaybackLauncher(Action<ProcessStartInfo> start,
        Func<string, IEnumerable<string>, string?> findExecutable)
    {
        _start = start;
        _findExecutable = findExecutable;
    }

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
                return LaunchWithShell(materializationResult.OutputPath);
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

        return PlaybackLaunchResult.Failure("未找到指定播放器。");
    }

    private PlaybackLaunchResult LaunchWithKnownPlayer(DiscoveredPlayer player, string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = player.Path,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(filePath);
            _start(startInfo);

            return PlaybackLaunchResult.Success(
                $"已使用 {player.DisplayName} 启动播放：{Path.GetFileName(filePath)}",
                player.DisplayName);
        }
        catch (Exception ex)
        {
            return PlaybackLaunchResult.Failure($"启动播放器失败：{ex.Message}", player.DisplayName);
        }
    }

    private PlaybackLaunchResult LaunchWithShell(string filePath)
    {
        try
        {
            ProcessStartInfo startInfo;
            if (OperatingSystem.IsLinux())
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add(filePath);
            }
            else if (OperatingSystem.IsMacOS())
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "open",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add(filePath);
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                };
            }

            _start(startInfo);
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

    private DiscoveredPlayer? DiscoverPlayer(PlayerKind kind)
    {
        var mpvExecutable = OperatingSystem.IsWindows() ? "mpv.exe" : "mpv";
        var vlcExecutable = OperatingSystem.IsWindows() ? "vlc.exe" : "vlc";
        var mpvFallbacks = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mpv", "mpv.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "mpv", "mpv.exe")
            }
            : Array.Empty<string>();
        var vlcFallbacks = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe")
            }
            : Array.Empty<string>();

        var candidates = new[]
        {
            new DiscoveredPlayer(
                PlayerKind.Mpv,
                "mpv",
                _findExecutable(mpvExecutable, mpvFallbacks)),
            new DiscoveredPlayer(
                PlayerKind.Vlc,
                "VLC",
                _findExecutable(vlcExecutable, vlcFallbacks))
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
