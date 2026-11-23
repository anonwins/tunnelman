using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
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
    private TunnelItem? _logItem;

    internal MainWindow(TunnelSession session)
    {
        _session = session;
        DataContext = session;
        InitializeComponent();
        LogCard.Visibility = Visibility.Collapsed;
        CommandBindings.Add(new CommandBinding(AddTunnelCommand, (s, e) => OnAddClick(s, e), (_, e) => e.CanExecute = true));
        CommandBindings.Add(new CommandBinding(ExportCommand, (_, _) => OnExportClick(), (_, e) => e.CanExecute = _session.HasTunnels));
        CommandBindings.Add(new CommandBinding(ImportCommand, (_, _) => OnImportClick(), (_, e) => e.CanExecute = true));
        CommandBindings.Add(new CommandBinding(HideCommand, (_, _) => HideToTray()));
        _session.PropertyChanged += OnSessionPropertyChanged;
        UpdateExportTip();
        UpdateLogVisibility();
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
    }

    public void HideToTray()
    {
        SaveUi();
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
            UpdateLogVisibility();
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
        if (CopyFeedback.TryCopy(item.ListenText, mark: null))
            CopyFeedback.FlashText(block);
        e.Handled = true;
    }

    private void OnRemoteCopy(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { DataContext: TunnelItem item } block)
            return;
        if (CopyFeedback.TryCopy(item.RemoteText, mark: null))
            CopyFeedback.FlashText(block);
        e.Handled = true;
    }

    private void OnTunnelListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is not null
            || FindAncestor<Thumb>(source) is not null
            || FindAncestor<RepeatButton>(source) is not null)
            return;
        if (TunnelList.ContainerFromElement(source) is not ListBoxItem)
            TunnelList.SelectedItem = null;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var current = start; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }

        return null;
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button)
            return;
        var text = TunnelList.SelectedItem is TunnelItem item ? item.LogText : "";
        CopyFeedback.TryCopy(text, CopyFeedback.FindMark(button));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_logItem is not null)
            _logItem.PropertyChanged -= OnSelectedLogChanged;
        _logItem = TunnelList.SelectedItem as TunnelItem;
        if (_logItem is not null)
            _logItem.PropertyChanged += OnSelectedLogChanged;
        UpdateLogVisibility();
        ScrollLog();
    }

    private void OnSelectedLogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TunnelItem.LogText))
            ScrollLog();
    }

    private void ScrollLog()
    {
        if (!LogBox.CheckAccess())
        {
            LogBox.Dispatcher.BeginInvoke(ScrollLog, DispatcherPriority.Background);
            return;
        }

        LogBox.CaretIndex = LogBox.Text.Length;
        LogBox.ScrollToEnd();
    }

    private void UpdateLogVisibility()
    {
        LogCard.Visibility = TunnelList.SelectedItem is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
