namespace BiliBiliLocalCacheManager.Wpf.Models;

public sealed record AutomaticMaintenanceEligibility(
    bool IsEligible,
    AppSettingsLoadKind LoadKind,
    int? SourceSchemaVersion,
    string? Reason,
    AppSettings? Settings = null)
{
    public static AutomaticMaintenanceEligibility EligibleCurrent { get; } = new(
        IsEligible: true,
        AppSettingsLoadKind.CurrentVersion,
        AppSettings.CurrentSchemaVersion,
        Reason: null);
}
