using System.Windows;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace BiliBiliLocalCacheManager.Wpf.Services;

/// <summary>
/// 基于 MessageBox 的对话框实现。
/// </summary>
public sealed class DialogService : IDialogService
{
    public bool Confirm(string message, string title)
    {
        var result = WpfMessageBox.Show(message, title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    public string? PickFolder(string title, string? initialPath)
    {
        // 使用 WinForms 的 FolderBrowserDialog，避免引入额外依赖
        using var dialog = new WinForms.FolderBrowserDialog();
        dialog.Description = title;
        dialog.UseDescriptionForTitle = true;
        dialog.SelectedPath = string.IsNullOrWhiteSpace(initialPath) ? string.Empty : initialPath;

        var result = dialog.ShowDialog();
        return result == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}