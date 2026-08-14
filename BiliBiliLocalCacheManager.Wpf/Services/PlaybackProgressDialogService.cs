using System.Windows;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public sealed class PlaybackProgressDialogService : IPlaybackProgressDialogService
{
    public async Task<PlaybackLaunchResult> RunAsync(
        string title,
        Func<IProgress<PlaybackPreparationProgress>, CancellationToken, Task<PlaybackLaunchResult>> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(operation);

        using var cancellationSource = new CancellationTokenSource();
        var window = new PlaybackProgressWindow(title);
        var owner = System.Windows.Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(candidate => candidate.IsActive) ??
            System.Windows.Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        void RequestCancellation(object? sender, EventArgs args)
        {
            if (cancellationSource.IsCancellationRequested)
            {
                return;
            }

            cancellationSource.Cancel();
            window.MarkCancelling();
        }

        var ownerWasEnabled = owner?.IsEnabled;
        window.CancellationRequested += RequestCancellation;
        var windowShown = false;
        var operationCompleted = false;

        try
        {
            var progress = new Progress<PlaybackPreparationProgress>(value =>
            {
                if (operationCompleted)
                {
                    return;
                }

                if (!windowShown)
                {
                    window.Show();
                    if (owner is not null)
                    {
                        owner.IsEnabled = false;
                    }

                    windowShown = true;
                }

                window.UpdateProgress(value);
            });
            return await operation(progress, cancellationSource.Token);
        }
        finally
        {
            operationCompleted = true;
            window.CancellationRequested -= RequestCancellation;
            if (windowShown && owner is not null && ownerWasEnabled.HasValue)
            {
                owner.IsEnabled = ownerWasEnabled.Value;
            }

            if (windowShown)
            {
                window.AllowClose();
                window.Close();
            }
        }
    }
}
