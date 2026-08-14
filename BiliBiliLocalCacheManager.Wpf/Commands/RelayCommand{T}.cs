using System.Windows.Input;

namespace BiliBiliLocalCacheManager.Wpf.Commands;

public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(GetParameter(parameter)) ?? true;

    public void Execute(object? parameter) => execute(GetParameter(parameter));

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? GetParameter(object? parameter)
    {
        if (parameter is null)
        {
            return default;
        }

        if (parameter is T value)
        {
            return value;
        }

        return default;
    }
}
