namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed record FfmpegDiagnosticSnapshot(
    bool IsInitialized,
    FfmpegResolutionSource Source,
    string? BinaryFolder,
    string? Version)
{
    public static FfmpegDiagnosticSnapshot NotInitialized { get; } = new(
        IsInitialized: false,
        FfmpegResolutionSource.NotInitialized,
        BinaryFolder: null,
        Version: null);
}
