using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

internal sealed record FfmpegBundleManifest(
    int SchemaVersion,
    string Provider,
    string Tag,
    string Asset,
    string Url,
    string Sha256)
{
    private const string EmbeddedResourceName =
        "BiliBiliLocalCacheManager.Playback.ffmpeg-bundle.json";
    private static readonly Regex Sha256Regex = new(
        "^[0-9a-fA-F]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseTokenRegex = new(
        "^[0-9A-Za-z._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static FfmpegBundleManifest Current { get; } = LoadEmbedded();

    internal static FfmpegBundleManifest Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        FfmpegBundleManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<FfmpegBundleManifest>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The FFmpeg bundle manifest is not valid JSON.", ex);
        }

        if (manifest is null)
        {
            throw new InvalidDataException("The FFmpeg bundle manifest is empty.");
        }

        return Validate(manifest);
    }

    private static FfmpegBundleManifest LoadEmbedded()
    {
        using var stream = typeof(FfmpegBundleManifest).Assembly
            .GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidDataException(
                $"Embedded FFmpeg bundle manifest was not found: {EmbeddedResourceName}");
        return Load(stream);
    }

    private static FfmpegBundleManifest Validate(FfmpegBundleManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported FFmpeg bundle manifest schema: {manifest.SchemaVersion}.");
        }

        if (!string.Equals(manifest.Provider, "BtbN/FFmpeg-Builds", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The FFmpeg bundle provider is not supported.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Tag) ||
            !ReleaseTokenRegex.IsMatch(manifest.Tag))
        {
            throw new InvalidDataException("The FFmpeg bundle tag is invalid.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Asset) ||
            !ReleaseTokenRegex.IsMatch(manifest.Asset) ||
            !string.Equals(Path.GetFileName(manifest.Asset), manifest.Asset, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The FFmpeg bundle asset name is invalid.");
        }

        var expectedUrl =
            $"https://github.com/{manifest.Provider}/releases/download/{manifest.Tag}/{manifest.Asset}";
        if (!string.Equals(manifest.Url, expectedUrl, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The FFmpeg bundle URL must exactly match its provider, tag, and asset.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256) ||
            !Sha256Regex.IsMatch(manifest.Sha256))
        {
            throw new InvalidDataException("The FFmpeg bundle SHA-256 is invalid.");
        }

        return manifest with { Sha256 = manifest.Sha256.ToLowerInvariant() };
    }
}
