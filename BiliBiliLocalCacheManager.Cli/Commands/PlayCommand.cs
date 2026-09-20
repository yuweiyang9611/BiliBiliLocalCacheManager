using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using CoreContracts = BiliBiliLocalCacheManager.Core.Application.Contracts;
using PlaybackContracts = BiliBiliLocalCacheManager.Playback.Contracts;

namespace BiliBiliLocalCacheManager.Cli.Commands;

public sealed partial class PlayCommand : ICommand
{
    private readonly CoreContracts.ICacheManager _cacheManager;
    private readonly Lazy<PlaybackContracts.ICachePlaybackService> _playbackService;

    public PlayCommand()
    {
        _cacheManager = new CacheManager();
        _playbackService = new Lazy<PlaybackContracts.ICachePlaybackService>(CreatePlaybackService);
    }

    public PlayCommand(CoreContracts.ICacheManager cacheManager, PlaybackContracts.ICachePlaybackService playbackService)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        ArgumentNullException.ThrowIfNull(playbackService);
        _playbackService = new Lazy<PlaybackContracts.ICachePlaybackService>(() => playbackService);
    }

    public int Execute(string[] args)
    {
        var specs = OptionParser.CreateCommonSpecs(includeIncomplete: true);
        specs["--segment"] = OptionParser.ValueOption("segment");
        specs["-s"] = OptionParser.ValueOption("segment");
        specs["--player"] = OptionParser.ValueOption("player");
        specs["-p"] = OptionParser.ValueOption("player");

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

        if (!AvidParser.TryParse(parsed.Positionals[0], out var avid))
        {
            CliPrinter.WriteError("无效的 avid，请输入正整数或 av 前缀编号。");
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
            var playbackService = _playbackService.Value;
            var pagePlan = playbackService.CreatePagePlan(cache, segmentKey);
            if (!pagePlan.IsPlayable)
            {
                CliPrinter.WriteError(pagePlan.SelectedPlan.Message ?? pagePlan.Message ?? "当前页面不可播放。");
                return 1;
            }

            var launchOptions = new PlaybackLaunchOptions
            {
                PreferredPlayer = ParsePlayerPreference(parsed.GetValue("player"))
            };

            var result = playbackService.Play(cache, segmentKey, launchOptions);
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
