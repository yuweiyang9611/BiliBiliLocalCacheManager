using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.ViewModels;

namespace BiliBiliLocalCacheManager.Wpf;

public partial class MainWindow : Window
{
    private bool _startupMaintenanceRequested;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        // Ctrl+F 聚焦关键字框：涉及具体控件焦点，放在视图层而不是 ViewModel。
        if (e.Key == Key.F &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            KeywordTextBox.Focus();
            KeywordTextBox.SelectAll();
            e.Handled = true;
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_startupMaintenanceRequested)
        {
            return;
        }

        _startupMaintenanceRequested = true;
        if (DataContext is MainViewModel viewModel)
        {
            _ = viewModel.StartBackgroundTranscodeCacheMaintenance();
            _ = viewModel.TryAutoScanOnStartupAsync();
        }
    }

    private void OnCacheRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        // 双击列头、滚动条等非数据行时不触发播放。
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<DataGridRow>(source) is null)
        {
            return;
        }

        if (viewModel.PlayBatchCommand.CanExecute(null))
        {
            viewModel.PlayBatchCommand.Execute(null);
        }
    }

    private void OnSegmentRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<DataGridRow>(source) is null)
        {
            return;
        }

        if (viewModel.PlaySelectedPageCommand.CanExecute(null))
        {
            viewModel.PlaySelectedPageCommand.Execute(null);
        }
    }

    private static T? FindAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            // VisualTreeHelper 只接受 Visual/Visual3D，命中 Run 这类非可视元素时改走逻辑树。
            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnDataGridRowPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row)
        {
            return;
        }

        row.Focus();

        if (row.IsSelected)
        {
            return;
        }

        if (ItemsControl.ItemsControlFromItemContainer(row) is System.Windows.Controls.DataGrid grid &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            grid.SelectedItems.Clear();
        }

        row.IsSelected = true;
    }

    private void OnCacheSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not System.Windows.Controls.DataGrid grid)
        {
            return;
        }

        viewModel.SetSelectedCaches(grid.SelectedItems.OfType<CacheItem>());
    }

    private void OnSegmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not System.Windows.Controls.DataGrid grid)
        {
            return;
        }

        viewModel.SetSelectedSegments(grid.SelectedItems.OfType<SegmentDetailItem>());
    }
}
