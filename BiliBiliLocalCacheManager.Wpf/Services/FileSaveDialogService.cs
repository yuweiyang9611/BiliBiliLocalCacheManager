namespace BiliBiliLocalCacheManager.Wpf.Services;

public sealed class FileSaveDialogService : IFileSaveDialogService
{
    public string? PickSavePath(
        string title,
        string defaultFileName,
        string defaultExtension,
        string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            DefaultExt = defaultExtension,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true
        };

        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is null
            ? dialog.ShowDialog()
            : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileName : null;
    }
}
