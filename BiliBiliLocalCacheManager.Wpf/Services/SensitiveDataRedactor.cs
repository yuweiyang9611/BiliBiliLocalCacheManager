using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public sealed class SensitiveDataRedactor : ISensitiveDataRedactor
{
    private static readonly string[] RedactionMarkers =
    [
        "[REDACTED]",
        "[TOKEN]",
        "[BVID]",
        "[AVID]",
        "[CACHE_ROOT]",
        "[TEMP]",
        "[LOCAL_APP_DATA]",
        "[USER_PROFILE]",
        "[PATH]",
        "<PATH>",
        "[URL]",
        "[MEDIA]"
    ];

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>\""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CredentialHeaderRegex = new(
        @"\b(authorization|cookie|set-cookie)\b\s*[:=]\s*[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SecretValueRegex = new(
        @"\b(access[_-]?token|refresh[_-]?token|token|signature|sig)\b\s*[:=]\s*(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s,;，；]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex BearerRegex = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex JwtRegex = new(
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TitleRegex = new(
        @"\b(title|video\s*title)\s*[:=：]\s*(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\r\n,;，；]+)|(?:视频标题|标题)\s*[:=：]\s*(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\r\n,;，；]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex BvidRegex = new(
        @"\bBV[0-9A-Za-z]{10}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AvidLabelRegex = new(
        @"\bavid\s*[:=：]?\s*\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AvidRegex = new(
        @"\bav\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex QuotedWindowsPathRegex = new(
        @"(?<=[\""'])(?:[A-Za-z]:[\\/]|\\\\)[^\""'\r\n]+(?=[\""'])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnquotedWindowsFilePathRegex = new(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\)[^\r\n,;，；。！？:：\""'<>|]*?\.[A-Za-z0-9]{1,16}(?=\s|[,;，；。！？:：)\]}]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnquotedWindowsDirectoryWithStatusRegex = new(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\)[^\r\n,;，；。！？:：\""'<>|]*?(?=\s+(?:不存在|无法访问|访问被拒绝|失败|未找到|被占用|does\s+not\s+exist|was\s+not\s+found|is\s+inaccessible|failed|cannot|could\s+not|access\s+denied)(?:[\s,;，；。！？:：]|$)|[,;，；。！？:：]|\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex WindowsPathRegex = new(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\)[^\s,;，；\]\[(){}<>\""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuotedUnixPathRegex = new(
        @"(?<=[\""'])/(?!/)[^\""'\r\n]+(?=[\""'])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnquotedUnixFilePathRegex = new(
        @"(?<![:/A-Za-z0-9_\]])/(?!/)[^\r\n,;，；。！？:：\""'<>|]*?\.[A-Za-z0-9]{1,16}(?=\s|[,;，；。！？:：)\]}]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnquotedUnixDirectoryWithStatusRegex = new(
        @"(?<![:/A-Za-z0-9_\]])/(?!/)[^\r\n,;，；。！？:：\""'<>|]*?(?=\s+(?:不存在|无法访问|访问被拒绝|失败|未找到|被占用|does\s+not\s+exist|was\s+not\s+found|is\s+inaccessible|failed|cannot|could\s+not|access\s+denied)(?:[\s,;，；。！？:：]|$)|[,;，；。！？:：]|\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex UnixPathRegex = new(
        @"(?<![:/A-Za-z0-9_\]])/(?!/)[^\s,;，；\]\[(){}<>\""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Redact(string value, SensitiveDataRedactionContext context)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(context);

        var redacted = UrlRegex.Replace(value, SanitizeUrl);
        redacted = CredentialHeaderRegex.Replace(
            redacted,
            match => $"{match.Groups[1].Value}=[REDACTED]");
        redacted = SecretValueRegex.Replace(
            redacted,
            match => $"{match.Groups[1].Value}=[REDACTED]");
        redacted = BearerRegex.Replace(redacted, "Bearer [REDACTED]");
        redacted = JwtRegex.Replace(redacted, "[TOKEN]");

        redacted = ReplaceKnownPath(
            redacted,
            context.CacheRoot,
            "[CACHE_ROOT]");
        redacted = ReplaceKnownPath(redacted, context.TemporaryDirectory, "[TEMP]");
        redacted = ReplaceKnownPath(
            redacted,
            context.LocalApplicationDataDirectory,
            "[LOCAL_APP_DATA]");
        redacted = ReplaceKnownPath(
            redacted,
            context.UserProfileDirectory,
            "[USER_PROFILE]");

        redacted = QuotedWindowsPathRegex.Replace(redacted, "<PATH>");
        redacted = UnquotedWindowsFilePathRegex.Replace(redacted, "<PATH>");
        redacted = UnquotedWindowsDirectoryWithStatusRegex.Replace(redacted, "<PATH>");
        redacted = WindowsPathRegex.Replace(redacted, "<PATH>");
        redacted = QuotedUnixPathRegex.Replace(redacted, "<PATH>");
        redacted = UnquotedUnixFilePathRegex.Replace(redacted, "<PATH>");
        redacted = UnquotedUnixDirectoryWithStatusRegex.Replace(redacted, "<PATH>");
        redacted = UnixPathRegex.Replace(redacted, "<PATH>");
        redacted = ReplaceKnownSensitiveValues(redacted, context.KnownSensitiveValues);
        redacted = TitleRegex.Replace(redacted, "title=[REDACTED]");
        redacted = BvidRegex.Replace(redacted, "[BVID]");
        redacted = AvidLabelRegex.Replace(redacted, "avid=[AVID]");
        return AvidRegex.Replace(redacted, "[AVID]");
    }

    private static string ReplaceKnownSensitiveValues(
        string value,
        IReadOnlyList<string> knownSensitiveValues)
    {
        if (knownSensitiveValues.Count == 0)
        {
            return value;
        }

        var replacementSpans = new List<ReplacementSpan>();
        foreach (var sensitiveValue in knownSensitiveValues)
        {
            var isSingleRune = TryGetSingleRune(sensitiveValue, out var rune);
            for (var searchIndex = 0; searchIndex <= value.Length - sensitiveValue.Length;)
            {
                var matchIndex = value.IndexOf(
                    sensitiveValue,
                    searchIndex,
                    StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    break;
                }

                var matchEnd = matchIndex + sensitiveValue.Length;
                if ((!isSingleRune ||
                     IsCjk(rune) ||
                     IsStandaloneSingleRuneMatch(value, matchIndex, matchEnd)) &&
                    !IsInsideRedactionMarker(value, matchIndex, matchEnd) &&
                    replacementSpans.All(existing =>
                        matchEnd <= existing.Start || matchIndex >= existing.End))
                {
                    replacementSpans.Add(new ReplacementSpan(matchIndex, matchEnd));
                }

                searchIndex = matchIndex + Math.Max(1, sensitiveValue.Length);
            }
        }

        if (replacementSpans.Count == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var sourceIndex = 0;
        foreach (var span in replacementSpans.OrderBy(span => span.Start))
        {
            builder.Append(value, sourceIndex, span.Start - sourceIndex);
            builder.Append("[MEDIA]");
            sourceIndex = span.End;
        }

        builder.Append(value, sourceIndex, value.Length - sourceIndex);
        return builder.ToString();
    }

    private static bool TryGetSingleRune(string value, out Rune rune)
    {
        var enumerator = value.EnumerateRunes().GetEnumerator();
        if (!enumerator.MoveNext())
        {
            rune = default;
            return false;
        }

        rune = enumerator.Current;
        return !enumerator.MoveNext();
    }

    private static bool IsStandaloneSingleRuneMatch(
        string value,
        int matchStart,
        int matchEnd)
    {
        return (matchStart == 0 || !char.IsLetterOrDigit(value, matchStart - 1)) &&
            (matchEnd == value.Length || !char.IsLetterOrDigit(value, matchEnd));
    }

    private static bool IsCjk(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x3134F or
            >= 0x2F800 and <= 0x2FA1F;
    }

    private static bool IsInsideRedactionMarker(
        string value,
        int matchStart,
        int matchEnd)
    {
        foreach (var marker in RedactionMarkers)
        {
            for (var markerStart = value.IndexOf(
                     marker,
                     StringComparison.Ordinal);
                 markerStart >= 0;
                 markerStart = value.IndexOf(
                     marker,
                     markerStart + marker.Length,
                     StringComparison.Ordinal))
            {
                if (matchStart >= markerStart &&
                    matchEnd <= markerStart + marker.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record ReplacementSpan(int Start, int End);

    private static string SanitizeUrl(Match match)
    {
        if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "[URL]";
        }

        var host = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{uri.Host}]"
            : uri.IdnHost;
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var pathMarker = uri.AbsolutePath == "/" ? string.Empty : "/[PATH]";
        return $"{uri.Scheme}://{host}{port}{pathMarker}";
    }

    private static string ReplaceKnownPath(string value, string? path, string marker)
    {
        var normalized = NormalizePath(path);
        if (normalized is null)
        {
            return value;
        }

        var result = ReplaceKnownPathVariant(value, normalized, marker);
        var alternate = normalized.Replace('\\', '/');
        if (!string.Equals(alternate, normalized, StringComparison.Ordinal))
        {
            result = ReplaceKnownPathVariant(result, alternate, marker);
        }

        return result;
    }

    private static string ReplaceKnownPathVariant(string value, string path, string marker)
    {
        const RegexOptions options =
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        var escaped = Regex.Escape(path);
        var endsWithSeparator = path.EndsWith('\\') || path.EndsWith('/');
        var childPrefix = endsWithSeparator ? escaped : escaped + @"[\\/]";
        var childMarker = marker + "/[PATH]";

        var result = Regex.Replace(
            value,
            $@"(?<=[\""']){childPrefix}[^\""'\r\n]+(?=[\""'])",
            childMarker,
            options);
        result = Regex.Replace(
            result,
            $@"{childPrefix}[^\r\n,;，；。！？:：\""'<>|]*?\.[A-Za-z0-9]{{1,16}}(?=\s|[,;，；。！？:：)\]}}]|$)",
            childMarker,
            options);
        result = Regex.Replace(
            result,
            $@"{childPrefix}[^\r\n,;，；。！？:：\""'<>|]*?(?=\s+(?:不存在|无法访问|访问被拒绝|失败|未找到|被占用|does\s+not\s+exist|was\s+not\s+found|is\s+inaccessible|failed|cannot|could\s+not|access\s+denied)(?:[\s,;，；。！？:：]|$)|[,;，；。！？:：]|\r?\n|$)",
            childMarker,
            options);
        result = Regex.Replace(
            result,
            $@"{childPrefix}[^\r\n,;，；。！？:：\""'<>|]+",
            childMarker,
            options);

        var root = Path.GetPathRoot(path);
        if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result,
                escaped +
                @"(?=$|[,;，；。！？:：)\]}]|\s+(?:不存在|无法访问|访问被拒绝|失败|未找到|被占用|does\s+not\s+exist|was\s+not\s+found|is\s+inaccessible|failed|cannot|could\s+not|access\s+denied)(?:[\s,;，；。！？:：]|$))",
                marker,
                options);
        }

        return result;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return null;
        }
    }
}
