using System.Windows.Input;

namespace BiliBiliLocalCacheManager.Wpf.Commands;

/// <summary>
/// 简单的命令实现，用于把按钮点击映射到 ViewModel 方法。
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    /// <summary>
    /// WPF 会监听该事件以决定是否需要重新评估 CanExecute。
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    /// <summary>
    /// 主动通知 WPF 重新评估 CanExecute。
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
