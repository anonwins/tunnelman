using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DragEventArgs = System.Windows.DragEventArgs;
using DragAction = System.Windows.DragAction;
using DragDropEffects = System.Windows.DragDropEffects;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using QueryContinueDragEventArgs = System.Windows.QueryContinueDragEventArgs;
using Microsoft.Win32;
using Tunnelman.Tunnels;

namespace Tunnelman;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand AddTunnelCommand = new();
    public static readonly RoutedCommand ExportCommand = new();
    public static readonly RoutedCommand ImportCommand = new();
    public static readonly RoutedCommand HideCommand = new();

    private readonly TunnelSession _session;
    private bool _allowClose;
    private bool _exitPromptOpen;
    private LogWindow? _logWindow;
    private bool _logHiddenWithOwner;
    private TunnelItem? _gripItem;
    private Point _gripStart;
    private bool _gripArmed;
    private ListBoxItem? _dragRow;
    private InsertAdorner? _insertAdorner;

    internal MainWindow(TunnelSession session)
    {
        _session = session;
        DataContext = session;
        InitializeComponent();
        ProfileButton.ToolTip = AppPaths.Directory;
        CommandBindings.Add(new CommandBinding(AddTunnelCommand, (s, e) => OnAddClick(s, e), (_, e) => e.CanExecute = true));
        CommandBindings.Add(new CommandBinding(ExportCommand, (_, _) => OnExportClick(), (_, e) => e.CanExecute = _session.HasTunnels));
        CommandBindings.Add(new CommandBinding(ImportCommand, (_, _) => OnImportClick(), (_, e) => e.CanExecute = true));
        CommandBindings.Add(new CommandBinding(HideCommand, (_, _) => HideToTray()));
        _session.PropertyChanged += OnSessionPropertyChanged;
        _session.Tunnels.CollectionChanged += (_, _) => CloseLogIfMissing();
        UpdateExportTip();
    }

    public void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
            WindowState = UiSettings.Current.Maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        if (_logHiddenWithOwner && _logWindow is not null)
        {
            _logHiddenWithOwner = false;
            _logWindow.Show();
        }
    }

    public void HideToTray()
    {
        SaveUi();
        if (_logWindow is { IsVisible: true })
        {
            _logHiddenWithOwner = true;
            _logWindow.Hide();
        }

        Hide();
        ShowInTaskbar = false;
    }

    public void AllowCloseForShutdown() => _allowClose = true;

    public async Task RequestExitAsync()
    {
        if (_allowClose || _exitPromptOpen)
            return;

        var needsPrompt = _session.RunningCount > 0 || _session.Tunnels.Count > 0;
        if (!needsPrompt)
        {
            await _session.StopAllAsync().ConfigureAwait(true);
            ShutdownApp();
            return;
        }

        _exitPromptOpen = true;
        try
        {
            var dialog = new ExitConfirmWindow(allowClear: true);
            if (IsVisible)
            {
                dialog.Owner = this;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.ShowInTaskbar = true;
            }

            if (dialog.ShowDialog() != true)
                return;

            if (dialog.ClearSession)
            {
                if (!await _session.ClearSessionAsync().ConfigureAwait(true))
                    return;
            }
            else
            {
                await _session.StopAllAsync().ConfigureAwait(true);
            }

            ShutdownApp();
        }
        finally
        {
            _exitPromptOpen = false;
        }
    }

    private void ShutdownApp()
    {
        if (IsVisible)
            SaveUi();
        _allowClose = true;
        Application.Current.Shutdown();
    }

    private void SaveUi()
    {
        if (!IsVisible)
            return;
        UiSettings.Current.Capture(this);
        UiSettings.Current.Save();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        UiSettings.Current.Apply(this);
        TitleBarDarkMode.Apply(this);
    }

    private void OnStateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            HideToTray();
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            if (IsVisible)
                SaveUi();
            return;
        }

        e.Cancel = true;
        _ = RequestExitAsync();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TunnelSession.HasTunnels) or null)
        {
            CommandManager.InvalidateRequerySuggested();
            UpdateExportTip();
        }
    }

    private void UpdateExportTip() =>
        ExportButton.ToolTip = _session.HasTunnels ? null : "Nothing to export";

    private void OnExportClick()
    {
        if (!_session.HasTunnels)
            return;
        var rows = TunnelTransfer.BuildExport(_session.SnapshotRecords());
        var picker = new TransferWindow(
            export: true,
            rows,
            _session.SnapshotRecords(),
            sourcePath: null,
            import: null,
            write: _session.WriteExport)
        {
            Owner = IsVisible ? this : null,
        };
        if (picker.ShowDialog() == true)
            _session.SetNotice(TunnelTransfer.CountLabel("Exported", picker.CompletedCount));
    }

    private async void OnImportClick()
    {
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import tunnels",
            Filter = TunnelTransfer.FileFilter,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (open.ShowDialog(IsVisible ? this : null) != true)
            return;

        var read = TunnelTransfer.Read(open.FileName);
        if (read.Status != ExportReadStatus.Ok)
        {
            MessageBox.Show(this, read.Error, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prepared = TunnelTransfer.PrepareImport(_session.SnapshotRecords(), read.Tunnels);
        var picker = new TransferWindow(export: false, prepared, _session.SnapshotRecords(), open.FileName, selected =>
            _session.TryImport(selected, out _, out var saveError) ? null : saveError)
        {
            Owner = IsVisible ? this : null,
        };
        if (picker.ShowDialog() == true)
            await _session.StartEnabledAsync().ConfigureAwait(true);
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new EditTunnelWindow { Owner = IsVisible ? this : null };
        if (dialog.ShowDialog() != true || dialog.Draft is null)
        {
            dialog.ReleaseDraft();
            return;
        }

        var draft = dialog.Draft;
        dialog.ReleaseDraft();
        if (!_session.TryAdd(draft, out var error))
        {
            MessageBox.Show(this, error, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TunnelItem item })
            return;
        var dialog = new EditTunnelWindow(item) { Owner = IsVisible ? this : null };
        if (dialog.ShowDialog() != true || dialog.Draft is null)
        {
            dialog.ReleaseDraft();
            return;
        }

        var draft = dialog.Draft;
        dialog.ReleaseDraft();
        try
        {
            await _session.ApplyEditAsync(item, draft).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TunnelItem item })
            return;
        await _session.DeleteAsync(item).ConfigureAwait(true);
        CloseLogIfMissing();
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: TunnelItem item })
            return;
        e.Handled = true;
        await _session.SetEnabledAsync(item, !item.Enabled).ConfigureAwait(true);
    }

    private void OnListenCopy(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { DataContext: TunnelItem item } block)
            return;
        if (CopyFeedback.TryCopy(item.ListenText))
            CopyFeedback.FlashText(block);
        e.Handled = true;
    }

    private void OnRemoteCopy(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { DataContext: TunnelItem item } block)
            return;
        if (CopyFeedback.TryCopy(item.RemoteText))
            CopyFeedback.FlashText(block);
        e.Handled = true;
    }

    private void OnOpenProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                "Could not open the profile folder.",
                "Tunnelman",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnLogClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TunnelItem item })
            return;
        if (_logWindow is null)
        {
            _logWindow = new LogWindow { Owner = this };
            _logWindow.Closed += (_, _) =>
            {
                _logWindow = null;
                _logHiddenWithOwner = false;
            };
            _logWindow.ShowTunnel(item, jumpToEnd: true);
            return;
        }

        var same = ReferenceEquals(_logWindow.Item, item);
        _logWindow.ShowTunnel(item, jumpToEnd: !same);
    }

    private void CloseLogIfMissing()
    {
        if (_logWindow?.Item is not TunnelItem item)
            return;
        if (_session.Tunnels.Contains(item))
            return;
        _logWindow.Close();
    }

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TunnelItem item } grip)
            return;
        _gripItem = item;
        _gripArmed = true;
        _gripStart = e.GetPosition(null);
        grip.CaptureMouse();
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        _gripArmed = false;
        _gripItem = null;
        if (sender is UIElement grip && grip.IsMouseCaptured)
            grip.ReleaseMouseCapture();
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_gripArmed || e.LeftButton != MouseButtonState.Pressed || _gripItem is not TunnelItem item)
            return;
        var point = e.GetPosition(null);
        if (Math.Abs(point.X - _gripStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _gripStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _gripArmed = false;
        _gripItem = null;
        if (sender is UIElement grip && grip.IsMouseCaptured)
            grip.ReleaseMouseCapture();

        if (TunnelList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row)
        {
            _dragRow = row;
            row.Opacity = 0.45;
        }

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Move);
        }
        finally
        {
            if (_dragRow is not null)
                _dragRow.Opacity = 1;
            _dragRow = null;
            ClearInsertLine();
        }
    }

    private void OnGripQueryContinue(object sender, QueryContinueDragEventArgs e)
    {
        if (!e.EscapePressed)
            return;
        e.Action = DragAction.Cancel;
        ClearInsertLine();
    }

    private void OnTunnelDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TunnelItem)) is not TunnelItem)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        var raw = RawInsertIndex(e.GetPosition(TunnelList));
        ShowInsertLine(raw);
        AutoScroll(e);
    }

    private void OnTunnelDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(TunnelList);
        if (point.X < 0 || point.Y < 0 || point.X > TunnelList.ActualWidth || point.Y > TunnelList.ActualHeight)
            ClearInsertLine();
    }

    private void OnTunnelDrop(object sender, DragEventArgs e)
    {
        ClearInsertLine();
        if (e.Data.GetData(typeof(TunnelItem)) is not TunnelItem item)
            return;
        var raw = RawInsertIndex(e.GetPosition(TunnelList));
        var old = _session.Tunnels.IndexOf(item);
        if (old < 0)
            return;
        var insert = raw;
        if (old < insert)
            insert--;
        insert = Math.Clamp(insert, 0, _session.Tunnels.Count - 1);
        _session.TryMove(item, insert);
        e.Handled = true;
    }

    private int RawInsertIndex(Point pointInList)
    {
        var count = TunnelList.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (TunnelList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row)
                continue;
            var top = row.TranslatePoint(new Point(0, 0), TunnelList).Y;
            if (pointInList.Y < top + row.ActualHeight / 2)
                return i;
        }

        return count;
    }

    private void ShowInsertLine(int rawIndex)
    {
        var layer = AdornerLayer.GetAdornerLayer(TunnelList);
        if (layer is null)
            return;
        if (_insertAdorner is null)
        {
            _insertAdorner = new InsertAdorner(TunnelList);
            layer.Add(_insertAdorner);
        }

        double y;
        var count = TunnelList.Items.Count;
        if (rawIndex >= count)
        {
            if (count == 0 || TunnelList.ItemContainerGenerator.ContainerFromIndex(count - 1) is not ListBoxItem last)
                return;
            y = last.TranslatePoint(new Point(0, last.ActualHeight), TunnelList).Y;
        }
        else if (TunnelList.ItemContainerGenerator.ContainerFromIndex(rawIndex) is ListBoxItem row)
        {
            y = row.TranslatePoint(new Point(0, 0), TunnelList).Y;
        }
        else
        {
            return;
        }

        _insertAdorner.SetY(y);
    }

    private void ClearInsertLine()
    {
        if (_insertAdorner is null)
            return;
        AdornerLayer.GetAdornerLayer(TunnelList)?.Remove(_insertAdorner);
        _insertAdorner = null;
    }

    private void AutoScroll(DragEventArgs e)
    {
        var viewer = FindScrollViewer(TunnelList);
        if (viewer is null)
            return;
        var point = e.GetPosition(viewer);
        const double edge = 24;
        if (point.Y < edge)
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - 16);
        else if (point.Y > viewer.ActualHeight - edge)
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + 16);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;
            var nested = FindScrollViewer(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private sealed class InsertAdorner(UIElement adorned) : Adorner(adorned)
    {
        private double _y;

        public void SetY(double y)
        {
            _y = y;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var brush = (Brush)FindResource("AccentBrush");
            var pen = new Pen(brush, 2);
            pen.Freeze();
            drawingContext.DrawLine(pen, new Point(8, _y), new Point(Math.Max(8, ActualWidth - 8), _y));
        }
    }
}
