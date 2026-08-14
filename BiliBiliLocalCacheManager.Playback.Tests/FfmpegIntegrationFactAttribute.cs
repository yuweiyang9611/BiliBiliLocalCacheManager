namespace BiliBiliLocalCacheManager.Playback.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class FfmpegIntegrationFactAttribute : FactAttribute
{
    public const string RunEnvironmentVariable =
        "BILIBILI_RUN_FFMPEG_INTEGRATION_TESTS";

    public FfmpegIntegrationFactAttribute()
    {
        if (!IsEnabled)
        {
            Skip = $"Set {RunEnvironmentVariable}=1 to run real FFmpeg integration tests.";
        }
    }

    public static bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable(RunEnvironmentVariable),
        "1",
        StringComparison.Ordinal);
}
