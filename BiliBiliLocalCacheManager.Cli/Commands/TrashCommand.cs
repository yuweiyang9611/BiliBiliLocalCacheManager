using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;

namespace BiliBiliLocalCacheManager.Cli.Commands;

/// <summary>
/// 应用回收站管理：list / restore / stats / purge。
/// </summary>
public sealed class TrashCommand : ICommand
{
    private readonly ICacheTrashService _trashService;

    public TrashCommand()
        : this(new FileSystemCacheTrashService())
    {
    }

    internal TrashCommand(ICacheTrashService trashService)
    {
        _trashService = trashService;
    }

    public int Execute(string[] args)
    {
        if (args.Length == 0)
        {
            CliPrinter.PrintTrashUsage();
            return 1;
        }

        var subCommand = args[0].ToLowerInvariant();
        if (subCommand is "--help" or "-h" or "help")
        {
            CliPrinter.PrintTrashUsage();
            return 0;
        }

        var rest = args.Skip(1).ToArray();
        return subCommand switch
        {
            "list" => ExecuteList(rest),
            "restore" => ExecuteRestore(rest),
            "stats" => ExecuteStats(rest),
            "purge" => ExecutePurge(rest),
            _ => UnknownSubCommand(subCommand)
        };
    }

    private static int UnknownSubCommand(string subCommand)
    {
        CliPrinter.WriteError($"未知的 trash 子命令：{subCommand}");
        var suggestion = CommandSuggestion.FindClosest(
            subCommand,
            ["list", "restore", "stats", "purge"]);
        if (suggestion is not null)
        {
            CliPrinter.WriteWarning($"你是不是想输入 trash {suggestion}？");
        }

        CliPrinter.PrintTrashUsage();
        return 1;
    }

    private int ExecuteList(string[] args)
    {
        if (!TryParseSimple(args, out var root, out _, out var exitCode))
        {
            return exitCode;
        }

        var entries = _trashService.ListEntries(root);
        CliPrinter.PrintTrashEntries(entries);
        return 0;
    }

    private int ExecuteStats(string[] args)
    {
        if (!TryParseSimple(args, out var root, out _, out var exitCode))
        {
            return exitCode;
        }

        var stats = _trashService.GetStatistics(root);
        CliPrinter.WriteLine($"回收站目录：{stats.TrashDirectory}");
        CliPrinter.WriteLine(
            $"受管条目 {stats.ManagedEntryCount.ToString(CultureInfo.InvariantCulture)} 条，" +
            $"{stats.FileCount.ToString(CultureInfo.InvariantCulture)} 个文件，" +
            $"共 {(stats.TotalBytes / (1024d * 1024)).ToString("F2", CultureInfo.InvariantCulture)} MB。");

        if (stats.UntrustedLegacyEntryCount > 0)
        {
            CliPrinter.WriteWarning(
                $"另有旧版未验证条目 {stats.UntrustedLegacyEntryCount.ToString(CultureInfo.InvariantCulture)} 条，" +
                "清理时需加 --include-untrusted。");
        }

        if (stats.PendingPurgeEntryCount > 0)
        {
            CliPrinter.WriteWarning(
                $"{stats.PendingPurgeEntryCount.ToString(CultureInfo.InvariantCulture)} 条已进入永久清理，无法还原。");
        }

        if (!string.IsNullOrWhiteSpace(stats.FirstErrorMessage))
        {
            CliPrinter.WriteWarning($"首个错误：{stats.FirstErrorMessage}");
            return 1;
        }

        return 0;
    }

    private int ExecuteRestore(string[] args)
    {
        var specs = BuildSpecs();
        OptionParser.ParsedArguments parsed;
        try
        {
            parsed = OptionParser.Parse(args, specs);
        }
        catch (ArgumentException ex)
        {
            CliPrinter.WriteError(ex.Message);
            CliPrinter.PrintTrashUsage();
            return 1;
        }

        if (parsed.Has("help"))
        {
            CliPrinter.PrintTrashUsage();
            return 0;
        }

        if (parsed.Positionals.Count != 1)
        {
            CliPrinter.WriteError("trash restore 需要且只需要一个 avid 参数。");
            CliPrinter.PrintTrashUsage();
            return 1;
        }

        if (!AvidParser.TryParse(parsed.Positionals[0], out var avid))
        {
            CliPrinter.WriteError("无效的 avid，请输入整数（也接受 av 前缀）。");
            return 1;
        }

        var (root, _) = OptionParser.ParseCommonOptions(parsed);
        if (string.IsNullOrWhiteSpace(root))
        {
            CliPrinter.WriteError("必须指定缓存根目录：--root <path>");
            return 1;
        }

        var candidates = _trashService.ListEntries(root)
            .Where(entry => entry.Avid == avid)
            .ToList();
        if (candidates.Count == 0)
        {
            CliPrinter.WriteError($"回收站中没有 avid = {avid.ToString(CultureInfo.InvariantCulture)} 的条目。");
            return 1;
        }

        // 同一个 avid 可能被删除过多次，默认还原最近的一条。
        var target = candidates[0];
        if (!target.IsRestorable)
        {
            CliPrinter.WriteError(
                $"该条目当前无法还原：{target.UnavailableReason ?? "未知原因"}");
            return 1;
        }

        if (candidates.Count > 1)
        {
            CliPrinter.WriteWarning(
                $"该 avid 在回收站中有 {candidates.Count.ToString(CultureInfo.InvariantCulture)} 条记录，" +
                $"将还原最近删除的一条（{target.DeletedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}）。");
        }

        var result = _trashService.Restore(root, avid, target.TrashPath);
        if (!result.Succeeded)
        {
            CliPrinter.WriteError($"还原失败：{result.ErrorMessage ?? "未知错误"}");
            return 1;
        }

        CliPrinter.WriteSuccess($"已还原 avid = {avid.ToString(CultureInfo.InvariantCulture)}：");
        CliPrinter.WriteLine(result.OriginalPath);
        return 0;
    }

    private int ExecutePurge(string[] args)
    {
        var specs = BuildSpecs();
        specs["--include-untrusted"] = OptionParser.FlagOption("include-untrusted");

        OptionParser.ParsedArguments parsed;
        try
        {
            parsed = OptionParser.Parse(args, specs);
        }
        catch (ArgumentException ex)
        {
            CliPrinter.WriteError(ex.Message);
            CliPrinter.PrintTrashUsage();
            return 1;
        }

        if (parsed.Has("help"))
        {
            CliPrinter.PrintTrashUsage();
            return 0;
        }

        var (root, _) = OptionParser.ParseCommonOptions(parsed);
        if (string.IsNullOrWhiteSpace(root))
        {
            CliPrinter.WriteError("必须指定缓存根目录：--root <path>");
            return 1;
        }

        var stats = _trashService.GetStatistics(root);
        if (stats.ManagedEntryCount == 0 && stats.UntrustedLegacyEntryCount == 0)
        {
            CliPrinter.WriteLine("应用回收站为空，无需清理。");
            return 0;
        }

        if (!parsed.Has("yes") &&
            !CliPrinter.Confirm(
                $"确定永久清空应用回收站吗？将删除 {stats.ManagedEntryCount.ToString(CultureInfo.InvariantCulture)} 条、" +
                $"释放 {(stats.TotalBytes / (1024d * 1024)).ToString("F2", CultureInfo.InvariantCulture)} MB，此操作不可撤销。",
                defaultValue: false))
        {
            CliPrinter.WriteLine("已取消。");
            return 0;
        }

        var result = _trashService.Purge(root, parsed.Has("include-untrusted"));
        CliPrinter.WriteSuccess(
            $"清理完成：删除 {result.DeletedEntryCount.ToString(CultureInfo.InvariantCulture)} 条，" +
            $"释放 {(result.FreedBytes / (1024d * 1024)).ToString("F2", CultureInfo.InvariantCulture)} MB，" +
            $"失败 {result.FailedEntryCount.ToString(CultureInfo.InvariantCulture)} 条，" +
            $"跳过 {result.SkippedEntryCount.ToString(CultureInfo.InvariantCulture)} 条。");

        if (!string.IsNullOrWhiteSpace(result.FirstErrorMessage))
        {
            CliPrinter.WriteWarning($"首个失败原因：{result.FirstErrorMessage}");
        }

        return result.FailedEntryCount > 0 ? 1 : 0;
    }

    private static bool TryParseSimple(
        string[] args,
        out string root,
        out OptionParser.ParsedArguments parsed,
        out int exitCode)
    {
        root = string.Empty;
        exitCode = 0;

        try
        {
            parsed = OptionParser.Parse(args, BuildSpecs());
        }
        catch (ArgumentException ex)
        {
            CliPrinter.WriteError(ex.Message);
            CliPrinter.PrintTrashUsage();
            parsed = null!;
            exitCode = 1;
            return false;
        }

        if (parsed.Has("help"))
        {
            CliPrinter.PrintTrashUsage();
            exitCode = 0;
            return false;
        }

        var (parsedRoot, _) = OptionParser.ParseCommonOptions(parsed);
        if (string.IsNullOrWhiteSpace(parsedRoot))
        {
            CliPrinter.WriteError("必须指定缓存根目录：--root <path>");
            exitCode = 1;
            return false;
        }

        root = parsedRoot;
        return true;
    }

    private static Dictionary<string, OptionParser.OptionSpec> BuildSpecs()
    {
        return new Dictionary<string, OptionParser.OptionSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["--root"] = OptionParser.ValueOption("root"),
            ["-r"] = OptionParser.ValueOption("root"),
            ["--yes"] = OptionParser.FlagOption("yes"),
            ["-y"] = OptionParser.FlagOption("yes"),
            ["--help"] = OptionParser.FlagOption("help"),
            ["-h"] = OptionParser.FlagOption("help")
        };
    }
}
