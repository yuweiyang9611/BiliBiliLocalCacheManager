using System.Globalization;

namespace BiliBiliLocalCacheManager.Cli;

/// <summary>
/// 解析用户输入的 avid。用户从 B 站复制来的往往带 "av" 前缀，直接拒绝会很别扭。
/// </summary>
public static class AvidParser
{
    public static bool TryParse(string? input, out long avid)
    {
        avid = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (text.StartsWith("av", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return long.TryParse(
                   text,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out avid) &&
               avid > 0;
    }
}
