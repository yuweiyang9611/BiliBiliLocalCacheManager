using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using CoreContracts = BiliBiliLocalCacheManager.Core.Application.Contracts;
using PlaybackContracts = BiliBiliLocalCacheManager.Playback.Contracts;

namespace BiliBiliLocalCacheManager.Cli.Commands;

public sealed partial class PlayCommand : ICommand
{
    private readonly CoreContracts.ICacheManager _cacheManager;
    private readonly PlaybackContracts.ICachePlaybackService _playbackService;

    public PlayCommand()
        : this(new CacheManager(), CreatePlaybackService())
    {
    }

    public PlayCommand(CoreContracts.ICacheManager cacheManager, PlaybackContracts.ICachePlaybackService playbackService)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
    }

    public int Execute(string[] args)
    {
        var specs = new Dictionary<string, OptionParser.OptionSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["--root"] = OptionParser.ValueOption("root"),
            ["-r"] = OptionParser.ValueOption("root"),
            ["--all"] = OptionParser.FlagOption("include-incomplete"),
            ["--include-incomplete"] = OptionParser.FlagOption("include-incomplete"),
            ["--segment"] = OptionParser.ValueOption("segment"),
            ["-s"] = OptionParser.ValueOption("segment"),
            ["--player"] = OptionParser.ValueOption("player"),
            ["-p"] = OptionParser.ValueOption("player"),
            ["--help"] = OptionParser.FlagOption("help"),
            ["-h"] = OptionParser.FlagOption("help")
        };

        OptionParser.ParsedArguments parsed;
        try
        {
            parsed = OptionParser.Parse(args, specs);
        }
        catch (ArgumentException ex)
        {
            CliPrinter.WriteError(ex.Message);
            CliPrinter.PrintPlayUsage();
            return 1;
        }

        if (parsed.Has("help"))
        {
            CliPrinter.PrintPlayUsage();
            return 0;
        }

        if (parsed.Positionals.Count == 0)
        {
            CliPrinter.WriteError("缺少 avid 参数。");
            CliPrinter.PrintPlayUsage();
            return 1;
        }

        if (parsed.Positionals.Count > 1)
        {
            CliPrinter.WriteError("avid 参数只能有一个，请检查是否多写了参数。");
            CliPrinter.PrintPlayUsage();
            return 1;
        }

        if (!long.TryParse(parsed.Positionals[0], NumberStyles.None, CultureInfo.InvariantCulture, out var avid))
        {
            CliPrinter.WriteError("无效的 avid，请输入整数。");
            CliPrinter.PrintPlayUsage();
            return 1;
        }

        var (root, options) = OptionParser.ParseCommonOptions(parsed);
        if (string.IsNullOrWhiteSpace(root))
        {
            CliPrinter.WriteError("必须指定缓存根目录：--root <path>");
            CliPrinter.PrintPlayUsage();
            return 1;
        }

        var cache = _cacheManager.FindByAvid(root, options, avid);
        if (cache is null)
        {
            CliPrinter.WriteError($"没有找到 avid = {avid} 对应的缓存。");
            return 1;
        }

        var segmentKey = parsed.GetValue("segment");
        var pageCount = cache.Segments
            .Select(segment => segment.PageIndex)
            .Distinct()
            .Count();

        if (string.IsNullOrWhiteSpace(segmentKey) && pageCount > 1)
        {
            CliPrinter.WriteError("该 avid 包含多个 page，请使用 --segment 指定页码或分段目录名。");
            CliPrinter.PrintAvailablePages(cache);
            return 1;
        }

        try
        {
            var pagePlan = _playbackService.CreatePagePlan(cache, segmentKey);
            if (!pagePlan.IsPlayable)
            {
                CliPrinter.WriteError(pagePlan.SelectedPlan.Message ?? pagePlan.Message ?? "当前页面不可播放。");
                return 1;
            }

            var launchOptions = new PlaybackLaunchOptions
            {
                PreferredPlayer = ParsePlayerPreference(parsed.GetValue("player"))
            };

            var result = _playbackService.Play(cache, segmentKey, launchOptions);
            if (!result.Succeeded)
            {
                CliPrinter.WriteError(result.Message);
                return 1;
            }

            CliPrinter.WriteSuccess(result.Message);
            return 0;
        }
        catch (ArgumentException ex)
        {
            CliPrinter.WriteError(ex.Message);
            CliPrinter.PrintAvailablePages(cache);
            return 1;
        }
    }

    private static PlaybackPlayerPreference ParsePlayerPreference(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return PlaybackPlayerPreference.SystemDefaultFirst;
        }

        return rawValue.Trim().ToLowerInvariant() switch
        {
            "system-first" or "default-first" or "auto" => PlaybackPlayerPreference.SystemDefaultFirst,
            "system" or "default" => PlaybackPlayerPreference.SystemDefaultOnly,
            "mpv" => PlaybackPlayerPreference.Mpv,
            "vlc" => PlaybackPlayerPreference.Vlc,
            _ => throw new ArgumentException($"未知播放器选项：{rawValue}")
        };
    }
}
