using System.IO;

namespace BiliBiliLocalCacheManager.Wpf.Services;

internal sealed record ApplicationStoragePaths(
    bool IsTestMode,
    string? SettingsPath,
    string? TranscodeCacheRoot)
{
    public static ApplicationStoragePaths Production { get; } = new(
        IsTestMode: false,
        SettingsPath: null,
        TranscodeCacheRoot: null);
}

internal static class ApplicationStoragePathResolver
{
    internal const string TestModeEnvironmentVariable =
        "BILIBILI_LOCAL_CACHE_MANAGER_TEST_MODE";
    internal const string SettingsPathEnvironmentVariable =
        "BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH";
    internal const string TranscodeCacheRootEnvironmentVariable =
        "BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT";

    public static ApplicationStoragePaths Resolve(
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        if (!string.Equals(
                getEnvironmentVariable(TestModeEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return ApplicationStoragePaths.Production;
        }

        var settingsPath = getEnvironmentVariable(SettingsPathEnvironmentVariable);
        var transcodeCacheRoot = getEnvironmentVariable(
            TranscodeCacheRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(settingsPath) ||
            string.IsNullOrWhiteSpace(transcodeCacheRoot))
        {
            throw new InvalidOperationException(
                $"{TestModeEnvironmentVariable}=1 requires both " +
                $"{SettingsPathEnvironmentVariable} and " +
                $"{TranscodeCacheRootEnvironmentVariable}.");
        }

        return new ApplicationStoragePaths(
            IsTestMode: true,
            SettingsPath: Path.GetFullPath(settingsPath),
            TranscodeCacheRoot: Path.GetFullPath(transcodeCacheRoot));
    }
}
