using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Services;

namespace BiliBiliLocalCacheManager.Cli.Commands;

public sealed class ShowCommand : ICommand
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
            CliPrinter.PrintShowUsage();
            return 1;
        }

        // 帮助参数优先
        if (parsed.Has("help"))
        {
            CliPrinter.PrintShowUsage();
            return 0;
        }

        // show 只允许一个位置参数：avid
        if (parsed.Positionals.Count == 0)
        {
            CliPrinter.WriteError("缺少 avid 参数。");
            CliPrinter.PrintShowUsage();
            return 1;
        }

        if (parsed.Positionals.Count > 1)
        {
            CliPrinter.WriteError("avid 参数只能有一个，请检查是否多写了参数。");
            CliPrinter.PrintShowUsage();
            return 1;
        }

        var avidArg = parsed.Positionals[0];
        if (!long.TryParse(avidArg, out var avid))
        {
            CliPrinter.WriteError("无效的 avid，请输入整数。");
            CliPrinter.PrintShowUsage();
            return 1;
        }

        // 解析通用参数（root + include-incomplete）
        var (root, options) = OptionParser.ParseCommonOptions(parsed);

        if (string.IsNullOrWhiteSpace(root))
        {
            CliPrinter.WriteError("必须指定缓存根目录：--root <path>");
            CliPrinter.PrintShowUsage();
            return 1;
        }

        // 扫描缓存并构建索引，再按 avid 查找
        ICacheManager manager = new CacheManager();
        var cache = manager.FindByAvid(root, options, avid);

        if (cache is null)
        {
            CliPrinter.WriteError($"没有找到 avid = {avid} 对应的缓存。");
            return 1;
        }

        CliPrinter.PrintCacheDetail(cache);
        return 0;
    }
}
