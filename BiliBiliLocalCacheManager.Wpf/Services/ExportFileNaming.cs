using System.Globalization;
using System.IO;
using System.Text;

namespace BiliBiliLocalCacheManager.Wpf.Services;

/// <summary>
/// 导出文件名生成：把缓存标题与分页信息变成 Windows 上合法、可读、不重名的文件名。
/// </summary>
public static class ExportFileNaming
{
    /// <summary>
    /// 基名长度上限。留出目录、" (12)" 去重后缀与扩展名的余量，避免触碰路径长度限制。
    /// </summary>
    private const int MaxBaseNameLength = 120;

    private const int MaxUniqueAttempts = 1000;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// 生成导出基名（不含扩展名）。单页视频只用标题，多页追加 "- P3 分段名"。
    /// </summary>
    public static string BuildBaseName(
        string? title,
        long avid,
        int pageIndex,
        string? partName,
        bool includePageSuffix)
    {
        var safeTitle = Sanitize(title);
        if (safeTitle.Length == 0)
        {
            safeTitle = "av" + avid.ToString(CultureInfo.InvariantCulture);
        }

        if (!includePageSuffix)
        {
            return Finalize(safeTitle, avid);
        }

        var pageToken = "P" + pageIndex.ToString(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(safeTitle);
        builder.Append(" - ").Append(pageToken);

        var safePart = Sanitize(partName);
        // 分段名与标题相同、或本身就是 "P3" 这样的页码时不再重复追加。
        if (safePart.Length > 0 &&
            !string.Equals(safePart, safeTitle, StringComparison.Ordinal) &&
            !string.Equals(safePart, pageToken, StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(' ').Append(safePart);
        }

        return Finalize(builder.ToString(), avid);
    }

    /// <summary>
    /// 去掉文件名非法字符、折叠空白，并处理 Windows 保留设备名。
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var ch in value)
        {
            var current = ch;
            if (Array.IndexOf(invalid, current) >= 0 || char.IsControl(current))
            {
                // 非法字符统一折成空格，随后与相邻空白一起折叠，避免出现 "__" 这类噪声。
                current = ' ';
            }

            if (char.IsWhiteSpace(current))
            {
                if (builder.Length == 0 || lastWasSpace)
                {
                    continue;
                }

                builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(current);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 在目标目录中找一个不与现有文件冲突的完整路径，重名时追加 " (2)"、" (3)"。
    /// </summary>
    public static string EnsureUniquePath(string directory, string baseName, string extension)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var normalizedExtension = NormalizeExtension(extension);
        var candidate = Path.Combine(directory, baseName + normalizedExtension);
        if (!PathExists(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; suffix < MaxUniqueAttempts; suffix++)
        {
            var suffixed = $"{baseName} ({suffix.ToString(CultureInfo.InvariantCulture)})";
            candidate = Path.Combine(directory, suffixed + normalizedExtension);
            if (!PathExists(candidate))
            {
                return candidate;
            }
        }

        // 极端情况下退化为时间戳，保证仍然能导出而不是直接失败。
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return Path.Combine(directory, $"{baseName} ({stamp}){normalizedExtension}");
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".mp4";
        }

        return extension.StartsWith('.') ? extension : "." + extension;
    }

    private static string Finalize(string value, long avid)
    {
        var trimmed = value.Length > MaxBaseNameLength
            ? value[..MaxBaseNameLength]
            : value;

        // Windows 不允许文件名以点或空格结尾，截断后可能正好落在这类字符上。
        trimmed = trimmed.TrimEnd('.', ' ');

        if (trimmed.Length == 0)
        {
            return "av" + avid.ToString(CultureInfo.InvariantCulture);
        }

        return ReservedDeviceNames.Contains(trimmed) ? "_" + trimmed : trimmed;
    }
}
