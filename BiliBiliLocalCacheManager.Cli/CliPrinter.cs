using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using Spectre.Console;

namespace BiliBiliLocalCacheManager.Cli;

public static class CliPrinter
{
    public static void WriteError(string message)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
    }

    public static void WriteSuccess(string message)
    {
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");
    }

    public static void WriteLine(string message)
    {
        AnsiConsole.MarkupLine(Markup.Escape(message));
    }

    public static void WriteWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// 交互确认。非交互环境（管道、CI、重定向输出）下无法提问，
    /// 一律按“未确认”处理，避免脚本里静默执行破坏性操作。
    /// </summary>
    public static bool Confirm(string question, bool defaultValue = true)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive || Console.IsInputRedirected)
        {
            WriteWarning($"{question}");
            WriteWarning("当前不是交互式终端，已按“取消”处理。确认执行请加 --yes。");
            return false;
        }

        return AnsiConsole.Prompt(
            new ConfirmationPrompt(Markup.Escape(question))
            {
                DefaultValue = defaultValue
            });
    }

    #region Usage / Help

    public static void PrintUsage()
    {
        WriteLine("BiliBili 缓存管理 CLI");
        WriteLine("");
        WriteLine("用法:");
        WriteLine("  BiliBiliLocalCacheManager.Cli <命令> [参数]");
        WriteLine("");
        WriteLine("可用命令:");
        WriteLine("  scan      扫描缓存根目录并列出所有 avid 的缓存信息");
        WriteLine("  show      显示某个 avid 的详细缓存信息");
        WriteLine("  play      播放某个 avid 的指定分段缓存");
        WriteLine("  delete    按 avid 删除缓存目录");
        WriteLine("  search    按标题/关键词搜索缓存");
        WriteLine("  help      显示帮助");
        WriteLine("");
        WriteLine("示例:");
        WriteLine("  BiliBiliLocalCacheManager.Cli scan --root \"D:\\BilibiliDownload\"");
        WriteLine("  BiliBiliLocalCacheManager.Cli scan --root \"D:\\BilibiliDownload\" --all");
        WriteLine("  BiliBiliLocalCacheManager.Cli delete 187742 --root \"D:\\BilibiliDownload\"");
        WriteLine("  BiliBiliLocalCacheManager.Cli show 187742 --root \"D:\\BilibiliDownload\"");
        WriteLine("  BiliBiliLocalCacheManager.Cli play 187742 --root \"D:\\BilibiliDownload\" --segment c_123456");
        WriteLine("  BiliBiliLocalCacheManager.Cli play 187742 --root \"D:\\BilibiliDownload\" --segment 1 --player system");
        WriteLine("  BiliBiliLocalCacheManager.Cli search \"猫猫\" --root \"D:\\BilibiliDownload\"");
        WriteLine("  BiliBiliLocalCacheManager.Cli help search");
        WriteLine("");
    }

    public static void PrintDeleteUsage()
    {
        WriteLine("用法:");
        WriteLine("  BiliBiliLocalCacheManager.Cli delete <avid> --root <path> [--permanent] [--yes] [--dry-run]");
        WriteLine("");
        WriteLine("参数:");
        WriteLine("  <avid>                  要删除的 avid（整数，也接受 av 前缀）");
        WriteLine("  --root, -r              B 站缓存根目录");
        WriteLine("  --permanent             永久删除而不是移入应用回收站（不可撤销）");
        WriteLine("  --yes, -y               跳过交互确认");
        WriteLine("  --dry-run               试运行，只打印将要处理的目录");
        WriteLine("  --help, -h              显示此命令帮助");
        WriteLine("");
        WriteLine("说明:");
        WriteLine("  默认移入应用回收站，可用 trash restore 还原；只有 --permanent 才会真正删除。");
        WriteLine("");
    }

    public static void PrintTrashUsage()
    {
        WriteLine("用法:");
        WriteLine("  BiliBiliLocalCacheManager.Cli trash list --root <path>");
        WriteLine("  BiliBiliLocalCacheManager.Cli trash restore <avid> --root <path>");
        WriteLine("  BiliBiliLocalCacheManager.Cli trash stats --root <path>");
        WriteLine("  BiliBiliLocalCacheManager.Cli trash purge --root <path> [--yes] [--include-untrusted]");
        WriteLine("");
        WriteLine("子命令:");
        WriteLine("  list                    列出应用回收站中的条目");
        WriteLine("  restore <avid>          把指定 avid 从回收站还原回原位置");
        WriteLine("  stats                   显示回收站占用统计");
        WriteLine("  purge                   永久清空回收站（不可撤销）");
        WriteLine("");
        WriteLine("参数:");
        WriteLine("  --root, -r              B 站缓存根目录");
        WriteLine("  --yes, -y               跳过交互确认");
        WriteLine("  --include-untrusted     purge 时一并删除缺少身份元数据的旧版条目");
        WriteLine("  --help, -h              显示此命令帮助");
        WriteLine("");
    }

    public static void PrintTrashEntries(IReadOnlyList<CacheTrashEntry> entries)
    {
        if (entries.Count == 0)
        {
            WriteLine("应用回收站为空。");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Avid");
        table.AddColumn("删除时间");
        table.AddColumn(new TableColumn("大小").RightAligned());
        table.AddColumn(new TableColumn("文件数").RightAligned());
        table.AddColumn("可还原");

        foreach (var entry in entries)
        {
            table.AddRow(
                Markup.Escape(entry.Avid.ToString(CultureInfo.InvariantCulture)),
                Markup.Escape(entry.DeletedAtUtc.ToLocalTime().ToString(
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.CurrentCulture)),
                Markup.Escape(FormatBytes(entry.TotalBytes)),
                Markup.Escape(entry.FileCount.ToString(CultureInfo.InvariantCulture)),
                entry.IsRestorable
                    ? "[green]是[/]"
                    : $"[yellow]否[/] {Markup.Escape(entry.UnavailableReason ?? string.Empty)}");
        }

        AnsiConsole.Write(table);
        WriteLine($"共 {entries.Count.ToString(CultureInfo.InvariantCulture)} 条。");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{(bytes / (1024d * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture)} GB";
        }

        return $"{(bytes / (1024d * 1024)).ToString("F2", CultureInfo.InvariantCulture)} MB";
    }

    public static void PrintScanUsage()
    {
        WriteLine("用法:");
        WriteLine("  BiliBiliLocalCacheManager.Cli scan --root <path> [--all]");
        WriteLine("");
        WriteLine("参数:");
        WriteLine("  --root, -r              B 站缓存根目录");
        WriteLine("  --all, --include-incomplete");
        WriteLine("                         包含未完成的缓存（默认只看完成的，可按需要在 Core 中调整默认值）");
        WriteLine("  --help, -h              显示此命令帮助");
        WriteLine("");
    }

    public static void PrintShowUsage()
    {
        WriteLine("用法:");
        WriteLine("  BiliBiliLocalCacheManager.Cli show <avid> --root <path> [--all]");
        WriteLine("");
        WriteLine("参数:");
        WriteLine("  <avid>                  要查看的 avid（整数）");
        WriteLine("  --root, -r              B 站缓存根目录");
        WriteLine("  --all, --include-incomplete");
        WriteLine("                         构建索引时包含未完成的缓存");
        WriteLine("  --help, -h              显示此命令帮助");
        WriteLine("");
    }

    public static void PrintSearchUsage()
    {
        WriteLine("用法:");
        WriteLine("  BiliBiliLocalCacheManager.Cli search <keyword> --root <path> [options]");
        WriteLine("");
        WriteLine("参数:");
        WriteLine("  <keyword>               要搜索的关键词");
        WriteLine("  --root, -r              B 站缓存根目录");
        WriteLine("  --all, --include-incomplete");
        WriteLine("                         构建索引时包含未完成的缓存");
        WriteLine("  --help, -h              显示此命令帮助");
        WriteLine("");
        WriteLine("搜索选项:");
        WriteLine("  --scope title,part,owner,bvid,avid");
        WriteLine("                         指定搜索范围（可用逗号分隔，使用时不与 --include-* 混用）");
        WriteLine("  --mode contains|equals|startswith|endswith");
        WriteLine("                         匹配模式（默认 contains）");
        WriteLine("  --case-sensitive        大小写敏感（默认不敏感）");
        WriteLine("  --split                 启用分词（默认启用）");
        WriteLine("  --no-split              禁用分词，把关键词当作整体");
        WriteLine("  --separators \",; \"      分词分隔符集合（字符串中的每个字符都会作为分隔符）");
        WriteLine("  --any                   任意关键词命中即可（OR 语义）");
        WriteLine("  --match-all             所有关键词必须命中（AND 语义，默认）");
        WriteLine("  --include-part-name     包含分段名（默认包含）");
        WriteLine("  --no-part-name          不包含分段名");
        WriteLine("  --include-owner-name    包含 UP 主");
        WriteLine("  --include-bvid          包含 Bvid");
        WriteLine("  --include-avid          包含 Avid");
        WriteLine("  注意: --all 仅表示包含未完成缓存，与关键词匹配逻辑无关");
        WriteLine("");
    }

    public static void PrintPlayUsage()
    {
        WriteLine("用法:");
        WriteLine("  BiliBiliLocalCacheManager.Cli play <avid> --root <path> [--segment <segment>] [--player <player>]");
        WriteLine("");
        WriteLine("参数:");
        WriteLine("  <avid>                  要播放的 avid（整数）");
        WriteLine("  --root, -r              B 站缓存根目录");
        WriteLine("  --segment, -s           指定页码或分段目录名，如 1 / 2 / c_123456");
        WriteLine("  --player, -p            播放器策略：system-first | system | mpv | vlc");
        WriteLine("  --all, --include-incomplete");
        WriteLine("                         构建索引时包含未完成的缓存");
        WriteLine("  --help, -h              显示此命令帮助");
        WriteLine("");
        WriteLine("说明:");
        WriteLine("  system-first            默认值。优先系统默认程序，必要时回退到 mpv / VLC");
        WriteLine("  system                  只使用系统默认程序");
        WriteLine("  mpv                     只使用 mpv");
        WriteLine("  vlc                     只使用 VLC");
        WriteLine("");
    }

    #endregion

    #region Scan Result

    public static void PrintScanResult(CacheIndex index)
    {
        if (index.VideoCaches.Count == 0)
        {
            WriteLine("没有在指定目录下找到任何缓存。");
            return;
        }

        var table = new Table
        {
            Border = TableBorder.Rounded
        };

        table.Expand();

        table.AddColumn("Avid");
        table.AddColumn("标题");
        table.AddColumn("分段数");
        table.AddColumn("大小(MB)");
        table.AddColumn("已完成");

        foreach (var cache in index.VideoCaches.OrderBy(c => c.Avid))
        {
            var sizeMb = cache.TotalSize / (1024d * 1024d);

            var title = cache.Title;
            const int maxTitleLen = 50;
            if (title.Length > maxTitleLen)
            {
                title = title[..(maxTitleLen - 1)] + "…";
            }

            table.AddRow(
                cache.Avid.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(title),
                cache.Segments.Count.ToString(CultureInfo.InvariantCulture),
                sizeMb.ToString("F2", CultureInfo.InvariantCulture),
                cache.IsAllCompleted ? "是" : "否"
            );
        }

        AnsiConsole.Write(table);
    }

    #endregion

    #region Cache Detail

    public static void PrintCacheDetail(BiliVideoCache cache)
    {
        // 概览 panel
        var infoTable = new Table().NoBorder();

        infoTable.AddColumn("字段");
        infoTable.AddColumn("值");

        infoTable.AddRow("Avid", cache.Avid.ToString(CultureInfo.InvariantCulture));
        infoTable.AddRow("标题", Markup.Escape(cache.Title));
        infoTable.AddRow("Bvid", Markup.Escape(cache.Bvid ?? "(无)"));
        infoTable.AddRow("UP 主",
            Markup.Escape($"{cache.OwnerName ?? "(未知)"} (ID: {cache.OwnerId?.ToString() ?? "?"})"));
        infoTable.AddRow("封面", Markup.Escape(cache.CoverUrl));
        infoTable.AddRow("总分段", cache.Segments.Count.ToString(CultureInfo.InvariantCulture));
        infoTable.AddRow("总大小(MB)", (cache.TotalSize / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture));
        infoTable.AddRow("总时长", cache.TotalDuration.ToString());
        infoTable.AddRow("全部完成", cache.IsAllCompleted ? "是" : "否");

        AnsiConsole.Write(new Panel(infoTable)
            .Header("缓存详情", Justify.Center)
            .RoundedBorder()
            .Expand());

        // 分段列表表格
        var segTable = new Table
        {
            Border = TableBorder.Rounded
        };
        segTable.Expand();

        segTable.AddColumn("Page");
        segTable.AddColumn("分段名");
        segTable.AddColumn("时长");
        segTable.AddColumn("大小(MB)");
        segTable.AddColumn("完成");
        segTable.AddColumn("目录");

        foreach (var seg in cache.Segments.OrderBy(s => s.PageIndex))
        {
            var part = seg.PartName;
            const int maxPartLen = 40;
            if (part.Length > maxPartLen)
            {
                part = part[..(maxPartLen - 1)] + "…";
            }

            var sizeMb = seg.TotalBytes / (1024d * 1024d);

            segTable.AddRow(
                seg.PageIndex.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(part),
                seg.TotalDuration.ToString(),
                sizeMb.ToString("F2", CultureInfo.InvariantCulture),
                seg.IsCompleted ? "是" : "否",
                Markup.Escape(seg.SegmentDirectory)
            );
        }

        AnsiConsole.Write(segTable);
    }

    public static void PrintAvailableSegments(BiliVideoCache cache)
    {
        var table = new Table
        {
            Border = TableBorder.Rounded
        };
        table.AddColumn("Segment");
        table.AddColumn("Page");
        table.AddColumn("分段名");

        foreach (var seg in cache.Segments.OrderBy(s => s.PageIndex).ThenBy(s => s.SegmentDirectory))
        {
            table.AddRow(
                Markup.Escape(Path.GetFileName(seg.SegmentDirectory)),
                seg.PageIndex.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(seg.PartName));
        }

        AnsiConsole.Write(table);
    }

    public static void PrintAvailablePages(BiliVideoCache cache)
    {
        var table = new Table
        {
            Border = TableBorder.Rounded
        };
        table.AddColumn("Page");
        table.AddColumn("候选分段数");
        table.AddColumn("候选目录");
        table.AddColumn("分段名");

        foreach (var group in cache.Segments
                     .GroupBy(segment => segment.PageIndex)
                     .OrderBy(group => group.Key))
        {
            var segments = group
                .OrderBy(segment => Path.GetFileName(segment.SegmentDirectory), StringComparer.OrdinalIgnoreCase)
                .ToList();

            table.AddRow(
                group.Key.ToString(CultureInfo.InvariantCulture),
                segments.Count.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(string.Join(", ", segments.Select(segment => Path.GetFileName(segment.SegmentDirectory)))),
                Markup.Escape(segments[0].PartName));
        }

        AnsiConsole.Write(table);
    }

    #endregion

    public static void PrintScanReport(CacheIndexBuildResult report)
    {
        ArgumentNullException.ThrowIfNull(report);

        WriteLine(
            $"扫描明细：分段 {report.IncludedEntries}，跳过未完成 {report.SkippedIncompleteEntries}，" +
            $"无效条目 {report.InvalidEntries}，不可访问目录 {report.InaccessibleDirectories}。");

        if (!report.HasWarnings)
        {
            return;
        }

        foreach (var issue in report.Issues.Take(10))
        {
            WriteError($"[{issue.Kind}] {issue.Path}: {issue.Message}");
        }

        if (report.InvalidEntries + report.InaccessibleDirectories > report.Issues.Count)
        {
            WriteLine("还有更多问题未逐条显示，请根据统计结果检查缓存目录。");
        }
    }
}
