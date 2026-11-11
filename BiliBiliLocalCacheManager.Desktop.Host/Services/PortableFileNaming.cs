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

    public static string BuildVideoDirectoryName(string title, long avid) =>
        BuildComponent(title, avid, string.Empty, $"_AV{avid.ToString(CultureInfo.InvariantCulture)}");

    public static string BuildPartFileName(string partName, long avid, int pageIndex) =>
        BuildComponent(partName, avid, $"P{pageIndex.ToString("D3", CultureInfo.InvariantCulture)}_", ".mp4");

    private static string BuildComponent(string title, long avid, string prefix, string suffix)
    {
        var safeTitle = Sanitize(title);
        var retained = new StringBuilder();
        var remainingBytes = 255 - Encoding.UTF8.GetByteCount(prefix + suffix);
        foreach (var rune in safeTitle.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > remainingBytes ||
                retained.Length + rune.Utf16SequenceLength > MaxBaseNameLength) break;
            retained.Append(rune.ToString());
            remainingBytes -= rune.Utf8SequenceLength;
        }
        var stem = retained.ToString().TrimEnd('.', ' ');
        if (stem.Length == 0) stem = "av" + avid.ToString(CultureInfo.InvariantCulture);
        if (ReservedDeviceNames.Contains(stem)) stem = "_" + stem;
        return prefix + stem + suffix;
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
