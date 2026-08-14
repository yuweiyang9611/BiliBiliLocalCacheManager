using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public interface IAppSettingsService
{
    AppSettings Load();

    AppSettingsLoadResult LoadWithReport()
    {
        return AppSettingsLoadResult.Current(Load());
    }

    AutomaticMaintenanceEligibility CheckAutomaticMaintenanceEligibility()
    {
        return AutomaticMaintenanceEligibility.EligibleCurrent;
    }

    void Save(AppSettings settings);
}
