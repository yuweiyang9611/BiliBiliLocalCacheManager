using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;

namespace BiliBiliLocalCacheManager.Cli.Commands;

public sealed class SearchCommand : ICommand
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
            ["--scope"] = OptionParser.ValueOption("scope"),
            ["--mode"] = OptionParser.ValueOption("mode"),
            ["--case-sensitive"] = OptionParser.FlagOption("case-sensitive"),
            ["--split"] = OptionParser.FlagOption("split"),
            ["--no-split"] = OptionParser.FlagOption("no-split"),
            ["--separators"] = OptionParser.ValueOption("separators"),
            ["--any"] = OptionParser.FlagOption("any"),
            ["--match-all"] = OptionParser.FlagOption("match-all"),
            ["--include-part-name"] = OptionParser.FlagOption("include-part-name"),
            ["--no-part-name"] = OptionParser.FlagOption("no-part-name"),
            ["--include-owner-name"] = OptionParser.FlagOption("include-owner-name"),
            ["--include-bvid"] = OptionParser.FlagOption("include-bvid"),
            ["--include-avid"] = OptionParser.FlagOption("include-avid"),
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
            CliPrinter.PrintSearchUsage();
            return 1;
        }

        // 帮助参数优先
        if (parsed.Has("help"))
        {
            CliPrinter.PrintSearchUsage();
            return 0;
        }

        // search 只允许一个位置参数：keyword
        if (parsed.Positionals.Count == 0)
        {
            CliPrinter.WriteError("缺少关键词参数。");
            CliPrinter.PrintSearchUsage();
            return 1;
        }

        if (parsed.Positionals.Count > 1)
        {
            CliPrinter.WriteError("关键词包含空格时请使用引号包裹。");
            CliPrinter.PrintSearchUsage();
            return 1;
        }

        var keywordArg = parsed.Positionals[0];
        if (string.IsNullOrWhiteSpace(keywordArg))
        {
            CliPrinter.WriteError("关键词不能为空。");
            CliPrinter.PrintSearchUsage();
            return 1;
        }

        // 解析通用参数（root + include-incomplete）
        var (root, buildOptions) = OptionParser.ParseCommonOptions(parsed);

        if (string.IsNullOrWhiteSpace(root))
        {
            CliPrinter.WriteError("必须指定缓存根目录：--root <path>");
            CliPrinter.PrintSearchUsage();
            return 1;
        }

        // 解析搜索相关参数（匹配模式/范围/分词等）
        CacheSearchOptions searchOptions;
        try
        {
            searchOptions = BuildSearchOptions(keywordArg, parsed);
        }
        catch (ArgumentException ex)
        {
            CliPrinter.WriteError(ex.Message);
            CliPrinter.PrintSearchUsage();
            return 1;
        }

        // 构建索引后执行搜索
        ICacheManager manager = new CacheManager();
        var results = manager.Search(root, buildOptions, searchOptions);
        if (results.Count == 0)
        {
            CliPrinter.WriteLine("没有找到匹配的缓存。");
            return 0;
        }

        // 复用现有表格展示逻辑（只展示命中的缓存集合）
        CliPrinter.PrintScanResult(new CacheIndex(results));
        return 0;
    }

    private static CacheSearchOptions BuildSearchOptions(string keyword, OptionParser.ParsedArguments parsed)
    {
        var caseSensitive = parsed.Has("case-sensitive");

        var splitKeywords = true;
        if (parsed.Has("no-split"))
        {
            splitKeywords = false;
        }
        else if (parsed.Has("split"))
        {
            splitKeywords = true;
        }

        var requireAllKeywords = true;
        if (parsed.Has("any"))
        {
            requireAllKeywords = false;
        }
        else if (parsed.Has("match-all"))
        {
            requireAllKeywords = true;
        }

        var matchMode = CacheSearchMatchMode.Contains;
        var modeValue = parsed.GetValue("mode");
        if (!string.IsNullOrWhiteSpace(modeValue))
        {
            matchMode = CacheSearchOptionsFactory.ParseMatchMode(modeValue);
        }

        var scope = BuildScope(parsed);

        var separatorsValue = parsed.GetValue("separators");
        var keywordSeparators = string.IsNullOrWhiteSpace(separatorsValue)
            ? null
            : separatorsValue.ToCharArray();

        return CacheSearchOptionsFactory.Create(
            keyword,
            matchMode,
            caseSensitive,
            splitKeywords,
            requireAllKeywords,
            scope,
            keywordSeparators);
    }

    private static CacheSearchScope BuildScope(OptionParser.ParsedArguments parsed)
    {
        var scopeValue = parsed.GetValue("scope");
        var hasScope = !string.IsNullOrWhiteSpace(scopeValue);
        var hasScopeFlags = parsed.Has("include-part-name")
            || parsed.Has("no-part-name")
            || parsed.Has("include-owner-name")
            || parsed.Has("include-bvid")
            || parsed.Has("include-avid");

        if (hasScope && hasScopeFlags)
        {
            throw new ArgumentException("不能同时使用 --scope 与 --include-*/--no-part-name 选项。");
        }

        if (parsed.Has("include-part-name") && parsed.Has("no-part-name"))
        {
            throw new ArgumentException("不能同时指定 --include-part-name 与 --no-part-name。");
        }

        if (hasScope)
        {
            return CacheSearchOptionsFactory.ParseScope(scopeValue!);
        }

        var includePartName = !parsed.Has("no-part-name");
        var includeOwnerName = parsed.Has("include-owner-name");
        var includeBvid = parsed.Has("include-bvid");
        var includeAvid = parsed.Has("include-avid");

        return CacheSearchOptionsFactory.BuildScope(
            includePartName,
            includeOwnerName,
            includeBvid,
            includeAvid);
    }
}
