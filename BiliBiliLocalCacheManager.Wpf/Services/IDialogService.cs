namespace BiliBiliLocalCacheManager.Wpf.Services;

/// <summary>
/// 对话框抽象，便于后续替换为自定义弹窗。
/// </summary>
public interface IDialogService
{
    bool Confirm(string message, string title);

    /// <summary>
    /// 打开文件夹选择对话框，返回用户选择的路径；取消时返回 null。
    /// </summary>
    string? PickFolder(string title, string? initialPath);
}
