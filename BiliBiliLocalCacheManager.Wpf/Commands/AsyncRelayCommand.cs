using System.Windows.Input;

namespace BiliBiliLocalCacheManager.Wpf.Commands;

/// <summary>
/// 支持异步执行的命令，执行期间自动禁用自身以避免重复触发。
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> execute,
    Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;

    /// <summary>
    /// 最近一次执行的任务，便于测试等待完成。
    /// </summary>
    public Task? ExecutionTask { get; private set; }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !_isExecuting && (canExecute?.Invoke() ?? true);
    }

    // ICommand requires void; keep async-void exception propagation while exposing the full
    // execution lifecycle through ExecutionTask for tests and command coordination.
    // ReSharper disable once AsyncVoidMethod
    public async void Execute(object? parameter)
    {
        if (_isExecuting)
        {
            return;
        }

        ExecutionTask = ExecuteCoreAsync();
        await ExecutionTask.ConfigureAwait(true);
    }

    private async Task ExecuteCoreAsync()
    {
        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 主动通知 WPF 重新评估 CanExecute。
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
