namespace BiliBiliLocalCacheManager.Wpf.Models;

public enum AppSettingsLoadKind
{
    MissingFile,
    LegacyVersion,
    CurrentVersion,
    FutureVersion,
    CorruptFile,
    ReadError
}

public sealed record AppSettingsLoadResult(
    AppSettingsLoadKind LoadKind,
    AppSettings Settings,
    int? SourceSchemaVersion,
    bool RequiresSave,
    bool IsUnsupported,
    bool CanSave,
    bool CanRunAutomaticMaintenance,
    IReadOnlyList<string> Adjustments,
    string? UserMessage)
{
    public static AppSettingsLoadResult Current(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AppSettingsLoadResult(
            AppSettingsLoadKind.CurrentVersion,
            settings,
            AppSettings.CurrentSchemaVersion,
            RequiresSave: false,
            IsUnsupported: false,
            CanSave: true,
            CanRunAutomaticMaintenance: true,
            Array.Empty<string>(),
            UserMessage: null);
    }

    public static AppSettingsLoadResult Missing(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AppSettingsLoadResult(
            AppSettingsLoadKind.MissingFile,
            settings,
            SourceSchemaVersion: null,
            RequiresSave: false,
            IsUnsupported: false,
            CanSave: true,
            CanRunAutomaticMaintenance: true,
            Array.Empty<string>(),
            UserMessage: null);
    }
}
