using System.Globalization;
using System.Text;

namespace BiliBiliLocalCacheManager.Desktop.Host.Services;

internal static class PortableFileNaming
{
    private const int MaxBaseNameLength = 120;
    private static readonly HashSet<char> InvalidCharacters =
    [
        '\\', '/', ':', '*', '?', '"', '<', '>', '|'
    ];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Build(
        string? title,
        long avid,
        int pageIndex,
        string? partName,
        bool includePage)
    {
        var safeTitle = Sanitize(title);
        if (safeTitle.Length == 0)
        {
            safeTitle = "av" + avid.ToString(CultureInfo.InvariantCulture);
        }

        var result = safeTitle;
        if (includePage)
        {
            var pageToken = "P" + pageIndex.ToString(CultureInfo.InvariantCulture);
            result += " - " + pageToken;
            var safePart = Sanitize(partName);
            if (safePart.Length > 0 &&
                !string.Equals(safePart, safeTitle, StringComparison.Ordinal) &&
                !string.Equals(safePart, pageToken, StringComparison.OrdinalIgnoreCase))
            {
                result += " " + safePart;
            }
        }

        result = result.Length > MaxBaseNameLength ? result[..MaxBaseNameLength] : result;
        result = result.TrimEnd('.', ' ');
        if (result.Length == 0)
        {
            result = "av" + avid.ToString(CultureInfo.InvariantCulture);
        }

        return ReservedDeviceNames.Contains(result) ? "_" + result : result;
    }

    public static string EnsureUnique(string directory, string baseName, string extension)
    {
        var candidate = Path.Combine(directory, baseName + extension);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({suffix}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(
            directory,
            $"{baseName} ({DateTime.Now:yyyyMMdd-HHmmss-fff}){extension}");
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousSpace = false;
        foreach (var character in value)
        {
            var normalized = InvalidCharacters.Contains(character) || char.IsControl(character)
                ? ' '
                : character;
            if (char.IsWhiteSpace(normalized))
            {
                if (builder.Length == 0 || previousSpace)
                {
                    continue;
                }

                builder.Append(' ');
                previousSpace = true;
                continue;
            }

            builder.Append(normalized);
            previousSpace = false;
        }

        return builder.ToString().Trim();
    }
}
