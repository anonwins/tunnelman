using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Tunnelman.Tunnels;

namespace Tunnelman;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand AddTunnelCommand = new();
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
        LogCard.Visibility = TunnelList.SelectedItem is null ? Visibility.Collapsed : Visibility.Visible;
        CommandBindings.Add(new CommandBinding(AddTunnelCommand, (s, e) => OnAddClick(s, e)));
        CommandBindings.Add(new CommandBinding(HideCommand, (_, _) => HideToTray()));
    }

    public void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    public void HideToTray()
    {
        SaveUi();
        WindowState = WindowState.Normal;
        Hide();
        ShowInTaskbar = false;
    }

    public void AllowCloseForShutdown() => _allowClose = true;

    public async Task RequestExitAsync()
    {
        if (_allowClose || _exitPromptOpen)
            return;

        if (_session.Tunnels.Count == 0)
        {
            await _session.StopAllAsync().ConfigureAwait(true);
            ShutdownApp();
            return;
        }

        _exitPromptOpen = true;
        try
        {
            var dialog = new ExitConfirmWindow { Owner = IsVisible ? this : null };
            if (dialog.ShowDialog() != true)
                return;

            if (dialog.ClearSession)
                await _session.ClearSessionAsync().ConfigureAwait(true);
            else
                await _session.StopAllAsync().ConfigureAwait(true);

            ShutdownApp();
        }
        finally
        {
            _exitPromptOpen = false;
        }
    }

    private void ShutdownApp()
    {
        SaveUi();
        _allowClose = true;
        Application.Current.Shutdown();
    }

    private void SaveUi()
    {
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
            SaveUi();
            return;
        }

        e.Cancel = true;
        _ = RequestExitAsync();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new EditTunnelWindow { Owner = IsVisible ? this : null };
        if (dialog.ShowDialog() != true || dialog.Options is null)
            return;
        if (!_session.TryAdd(dialog.TunnelName, dialog.Kind, dialog.Options, out var error))
        {
            MessageBox.Show(this, error, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TunnelItem item })
            return;
        var dialog = new EditTunnelWindow(item) { Owner = IsVisible ? this : null };
        if (dialog.ShowDialog() != true || dialog.Options is null)
            return;
        try
        {
            await _session.ApplyEditAsync(item, dialog.TunnelName, dialog.Kind, dialog.Options).ConfigureAwait(true);
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

    private void OnNcatChipClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NcatMissingWindow { Owner = IsVisible ? this : null };
        dialog.ShowDialog();
        _session.ResolveNcat();
        if (!_session.IsNcatMissing)
            _ = _session.StartEnabledAsync();
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
        if (TunnelList.ContainerFromElement(source) is not ListBoxItem)
            TunnelList.SelectedItem = null;
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
        LogCard.Visibility = TunnelList.SelectedItem is null ? Visibility.Collapsed : Visibility.Visible;
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
}
