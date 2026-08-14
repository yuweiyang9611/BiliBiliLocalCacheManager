using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Cli;

/// <summary>
/// CLI 通用参数解析器：
/// 1) 统一处理 --key value / --key=value / -k value 三种形式；
/// 2) 支持参数合法性校验，避免“未知参数被静默忽略”的问题；
/// 3) 将解析结果标准化为「位置参数 + 选项字典」。
/// </summary>
public static class OptionParser
{
    /// <summary>
    /// 单个选项的定义。
    /// </summary>
    public sealed class OptionSpec(string canonicalName, bool requiresValue)
    {
        /// <summary>
        /// 选项规范名（用于在解析结果中统一取值）。
        /// </summary>
        public string CanonicalName { get; } = canonicalName;

        /// <summary>
        /// 是否需要一个显式的值。
        /// </summary>
        public bool RequiresValue { get; } = requiresValue;
    }

    /// <summary>
    /// 解析结果：位置参数 + 选项（含 flag）。
    /// </summary>
    public sealed class ParsedArguments(
        IReadOnlyList<string> positionals,
        IReadOnlyDictionary<string, string?> options)
    {
        public IReadOnlyList<string> Positionals { get; } = positionals;
        public IReadOnlyDictionary<string, string?> Options { get; } = options;

        /// <summary>
        /// 判断某个规范名选项是否出现。
        /// </summary>
        public bool Has(string canonicalName) => Options.ContainsKey(canonicalName);

        /// <summary>
        /// 获取某个规范名选项的值（无值选项返回 null）。
        /// </summary>
        public string? GetValue(string canonicalName) =>
            Options.TryGetValue(canonicalName, out var value) ? value : null;
    }

    /// <summary>
    /// 解析参数，要求所有选项都必须在 specs 中定义。
    /// </summary>
    public static ParsedArguments Parse(string[] args, IReadOnlyDictionary<string, OptionSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(specs);

        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // 使用 -- 表示“后续全部当作位置参数”
            if (arg == "--")
            {
                for (var j = i + 1; j < args.Length; j++)
                {
                    positionals.Add(args[j]);
                }

                break;
            }

            // 非 - 开头的一律当作位置参数
            if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            // 处理 --key=value 形式
            string name = arg;
            string? value = null;
            var equalIndex = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equalIndex > 2)
            {
                name = arg[..equalIndex];
                value = arg[(equalIndex + 1)..];
            }

            if (!specs.TryGetValue(name, out var spec))
            {
                throw new ArgumentException($"未知参数：{name}");
            }

            if (spec.RequiresValue)
            {
                // 没有在同一个 token 中给值，就尝试取下一个 token。
                if (value is null)
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"参数 {name} 需要一个值。");
                    }

                    // 如果下一个 token 看起来像另一个选项，则认为当前值缺失。
                    if (args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"参数 {name} 需要一个值。");
                    }

                    value = args[++i];
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException($"参数 {name} 的值不能为空。");
                }

                options[spec.CanonicalName] = value;
                continue;
            }

            // 不需要值的选项（flag）直接标记出现。
            options[spec.CanonicalName] = null;
        }

        return new ParsedArguments(positionals, options);
    }

    /// <summary>
    /// 从已解析结果中提取通用参数（root + include-incomplete）。
    /// </summary>
    public static (string root, CacheIndexBuildOptions options) ParseCommonOptions(ParsedArguments parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var options = new CacheIndexBuildOptions
        {
            IncludeIncompleteEntries = false
        };

        // 统一处理缓存根目录参数
        var root = parsed.GetValue("root") ?? string.Empty;

        // 统一处理是否包含未完成缓存
        if (parsed.Has("include-incomplete"))
        {
            options.IncludeIncompleteEntries = true;
        }

        return (root, options);
    }

    /// <summary>
    /// 便捷构造：需要值的选项。
    /// </summary>
    public static OptionSpec ValueOption(string canonicalName) => new(canonicalName, true);

    /// <summary>
    /// 便捷构造：不需要值的选项（flag）。
    /// </summary>
    public static OptionSpec FlagOption(string canonicalName) => new(canonicalName, false);
}
