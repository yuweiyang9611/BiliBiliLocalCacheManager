using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Services;

namespace BiliBiliLocalCacheManager.Cli.Commands;

public sealed class ScanCommand : ICommand
{
    public int Execute(string[] args)
    {
        // 定义本命令允许的参数，未知参数会直接报错，避免“无效参数被忽略”的误解。
        var specs = new Dictionary<string, OptionParser.OptionSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["--root"] = OptionParser.ValueOption("root"),
            ["-r"] = OptionParser.ValueOption("root"),
            ["--all"] = OptionParser.FlagOption("include-incomplete"),
            ["--include-incomplete"] = OptionParser.FlagOption("include-incomplete"),
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
            CliPrinter.PrintScanUsage();
            return 1;
        }

        // 帮助参数优先
        if (parsed.Has("help"))
        {
            CliPrinter.PrintScanUsage();
            return 0;
        }

        // scan 不接受位置参数，避免误把目录当成关键词等场景。
        if (parsed.Positionals.Count > 0)
        {
            CliPrinter.WriteError("scan 命令不接受位置参数，请使用 --root 指定目录。");
            CliPrinter.PrintScanUsage();
            return 1;
        }

        var (root, options) = OptionParser.ParseCommonOptions(parsed);

        if (string.IsNullOrWhiteSpace(root))
        {
            CliPrinter.WriteError("必须指定缓存根目录：--root <path>");
            CliPrinter.PrintScanUsage();
            return 1;
        }

        // 扫描缓存目录并构建索引
        ICacheManager manager = new CacheManager();
        var report = manager.BuildIndexWithReport(root, options);

        CliPrinter.PrintScanResult(report.Index);
        CliPrinter.PrintScanReport(report);
        return 0;
    }
}
