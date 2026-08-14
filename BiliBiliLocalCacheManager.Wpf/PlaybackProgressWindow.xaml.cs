using System.ComponentModel;
using System.Windows;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Wpf;

public partial class PlaybackProgressWindow : Window
{
    private bool _allowClose;
    private bool _cancellationRequested;

    public PlaybackProgressWindow(string title)
    {
        InitializeComponent();
        Title = title;
    }

    public event EventHandler? CancellationRequested;

    public void UpdateProgress(PlaybackPreparationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        StageTextBlock.Text = progress.Stage;
        ElapsedTextBlock.Text = $"\u5df2\u7528\u65f6\uff1a{FormatDuration(progress.Elapsed)}";

        if (progress.Percentage is { } percentage)
        {
            PreparationProgressBar.IsIndeterminate = false;
            PreparationProgressBar.Value = percentage;
            PercentTextBlock.Text = $"{percentage:0}%";
        }
        else
        {
            PreparationProgressBar.IsIndeterminate = true;
            PercentTextBlock.Text = "\u6b63\u5728\u8ba1\u7b97\u8fdb\u5ea6\u2026";
        }

        RemainingTextBlock.Text = progress.EstimatedRemaining is { } remaining
            ? $"\u9884\u8ba1\u5269\u4f59\uff1a{FormatDuration(remaining)}"
            : "\u9884\u8ba1\u5269\u4f59\uff1a\u8ba1\u7b97\u4e2d\u2026";
    }

    public void MarkCancelling()
    {
        if (_cancellationRequested)
        {
            return;
        }

        _cancellationRequested = true;
        StageTextBlock.Text = "\u6b63\u5728\u53d6\u6d88 FFmpeg\uff0c\u8bf7\u7a0d\u5019\u2026";
        RemainingTextBlock.Text = "\u5df2\u8bf7\u6c42\u53d6\u6d88\uff0c\u4e0d\u4f1a\u542f\u52a8\u64ad\u653e\u5668\u3002";
        CancelButton.IsEnabled = false;
        CancelButton.Content = "\u6b63\u5728\u53d6\u6d88\u2026";
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        RequestCancellation();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        if (_cancellationRequested)
        {
            return;
        }

        CancellationRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var normalized = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return normalized.TotalHours >= 1
            ? $"{(int)normalized.TotalHours:00}:{normalized.Minutes:00}:{normalized.Seconds:00}"
            : $"{normalized.Minutes:00}:{normalized.Seconds:00}";
    }
}
