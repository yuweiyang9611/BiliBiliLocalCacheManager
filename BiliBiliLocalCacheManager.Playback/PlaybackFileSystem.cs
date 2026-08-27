namespace BiliBiliLocalCacheManager.Playback;

internal static class PlaybackFileSystem
{
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
