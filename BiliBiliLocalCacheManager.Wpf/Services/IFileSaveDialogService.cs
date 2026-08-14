namespace BiliBiliLocalCacheManager.Wpf.Services;

public interface IFileSaveDialogService
{
    string? PickSavePath(
        string title,
        string defaultFileName,
        string defaultExtension,
        string filter);
}
