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

    private static readonly (int Seconds, string Label)[] AutoLockChoices =
    [
        (0, "Don't auto-lock"),
        (30, "After 30 seconds idle"),
        (60, "After 1 minute idle"),
        (300, "After 5 minutes idle"),
        (900, "After 15 minutes idle"),
    ];

    private readonly TunnelSession _session;
    private readonly DispatcherTimer _idleTimer;
    private bool _allowClose;
    private bool _exitPromptOpen;
    private bool _gateBusy;
    private bool _loadingAutoLock = true;
    private bool _locking;
    private DateTime _lastInputUtc = DateTime.UtcNow;
    private TunnelItem? _logItem;

    internal MainWindow(TunnelSession session)
    {
        _session = session;
        DataContext = session;
        InitializeComponent();
        foreach (var choice in AutoLockChoices)
            AutoLockBox.Items.Add(choice.Label);
        var selected = UiSettings.NormalizeAutoLock(UiSettings.Current.AutoLockSeconds);
        var index = Array.FindIndex(AutoLockChoices, choice => choice.Seconds == selected);
        AutoLockBox.SelectedIndex = index < 0 ? 0 : index;
        _loadingAutoLock = false;
        LogCard.Visibility = Visibility.Collapsed;
        GateButtonText.Text = _session.ShowConfirm ? "Create session" : "Unlock";
        ConfirmBox.Visibility = _session.ShowConfirm ? Visibility.Visible : Visibility.Collapsed;
        CommandBindings.Add(new CommandBinding(AddTunnelCommand, (s, e) => OnAddClick(s, e), (_, e) => e.CanExecute = _session.ShowSession));
        CommandBindings.Add(new CommandBinding(ExportCommand, (_, _) => OnExportClick(), (_, e) => e.CanExecute = _session.HasTunnels));
        CommandBindings.Add(new CommandBinding(ImportCommand, (_, _) => OnImportClick(), (_, e) => e.CanExecute = _session.ShowSession));
        CommandBindings.Add(new CommandBinding(HideCommand, (_, _) => HideToTray()));
        _session.PropertyChanged += OnSessionPropertyChanged;
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += OnIdleTick;
        Loaded += OnLoaded;
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
        NoteInput();
        FocusGate();
    }

    public void HideToTray()
    {
        SaveUi();
        Hide();
        ShowInTaskbar = false;
        LockIfHidden();
    }

    private void LockIfHidden()
    {
        if (UiSettings.Current.AutoLockSeconds <= 0 || IsVisible || _allowClose || !_session.CanLock)
            return;
        BeginLock();
    }

    public void AllowCloseForShutdown() => _allowClose = true;

    public async Task RequestExitAsync()
    {
        if (_allowClose || _exitPromptOpen)
            return;

        var needsPrompt = _session.RunningCount > 0 || (_session.ShowSession && _session.Tunnels.Count > 0);
        if (!needsPrompt)
        {
            await _session.StopAllAsync().ConfigureAwait(true);
            ShutdownApp();
            return;
        }

        _exitPromptOpen = true;
        _session.SetExitPrompt(true);
        try
        {
            var dialog = new ExitConfirmWindow(allowClear: _session.ShowSession);
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
            _session.SetExitPrompt(false);
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
        if (e.PropertyName is nameof(TunnelSession.HasTunnels) or nameof(TunnelSession.ShowSession) or null)
        {
            CommandManager.InvalidateRequerySuggested();
            UpdateExportTip();
            UpdateLogVisibility();
            ConfirmBox.Visibility = _session.ShowConfirm ? Visibility.Visible : Visibility.Collapsed;
            if (!_gateBusy)
                GateButtonText.Text = _session.ShowConfirm ? "Create session" : "Unlock";
            if (_session.ShowSession)
                NoteInput();
            EnsureIdleTimer();
            if (_session.ShowGate)
                FocusGate();
        }

        if (e.PropertyName == nameof(TunnelSession.CanLock))
            LockIfHidden();
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

    private void OnImportClick()
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
        if (_session.Key is not { } key)
        {
            MessageBox.Show(this, TunnelTransfer.CouldNotRead, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var read = TunnelTransfer.Read(open.FileName, key);
        IReadOnlyList<TunnelRecord> records;
        if (read.Status == ExportReadStatus.Ok)
        {
            records = read.Tunnels;
        }
        else if (read.Status == ExportReadStatus.WrongPassphrase)
        {
            var unlock = new ExportUnlockWindow(
                read.Salt ?? [],
                read.Iterations,
                derived => TunnelTransfer.Read(open.FileName, derived));
            if (IsVisible)
            {
                unlock.Owner = this;
            }
            else
            {
                unlock.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                unlock.ShowInTaskbar = true;
            }

            if (unlock.ShowDialog() != true)
            {
                if (unlock.Failure is { } failure)
                    MessageBox.Show(this, failure, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            records = unlock.Records;
        }
        else
        {
            MessageBox.Show(this, read.Error, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prepared = TunnelTransfer.PrepareImport(_session.SnapshotRecords(), records);
        var picker = new TransferWindow(export: false, prepared, _session.SnapshotRecords(), open.FileName, selected =>
            _session.TryImport(selected, out _, out var saveError) ? null : saveError)
        {
            Owner = IsVisible ? this : null,
        };
        picker.ShowDialog();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new EditTunnelWindow { Owner = IsVisible ? this : null };
        if (dialog.ShowDialog() != true || dialog.Draft is null)
            return;
        if (!_session.TryAdd(dialog.Draft, out var error))
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
            return;
        try
        {
            await _session.ApplyEditAsync(item, dialog.Draft).ConfigureAwait(true);
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        Closed += (_, _) => InputManager.Current.PreProcessInput -= OnPreProcessInput;
        NoteInput();
        FocusGate();
        EnsureIdleTimer();
    }

    private void OnPreProcessInput(object? sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is System.Windows.Input.KeyEventArgs
            or System.Windows.Input.MouseEventArgs
            or TouchEventArgs
            or StylusEventArgs)
            NoteInput();
    }

    private void NoteInput() => _lastInputUtc = DateTime.UtcNow;

    private void EnsureIdleTimer()
    {
        if (!_session.ShowSession || UiSettings.Current.AutoLockSeconds <= 0)
        {
            _idleTimer.Stop();
            return;
        }

        if (!_idleTimer.IsEnabled)
            _idleTimer.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (!_session.ShowSession || UiSettings.Current.AutoLockSeconds <= 0)
        {
            _idleTimer.Stop();
            return;
        }

        if (DateTime.UtcNow - _lastInputUtc < TimeSpan.FromSeconds(UiSettings.Current.AutoLockSeconds))
            return;
        if (!_session.CanLock || _locking)
            return;
        BeginLock();
    }

    private void OnAutoLockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAutoLock || AutoLockBox.SelectedIndex < 0)
            return;
        UiSettings.Current.SetAutoLockSeconds(AutoLockChoices[AutoLockBox.SelectedIndex].Seconds);
        NoteInput();
        EnsureIdleTimer();
    }

    private void OnLockClick(object sender, RoutedEventArgs e) => BeginLock();

    private void BeginLock()
    {
        if (_locking || !_session.CanLock)
            return;
        var closed = false;
        foreach (Window window in OwnedWindows)
        {
            if (!window.IsVisible)
                continue;
            try
            {
                window.DialogResult = false;
                closed = true;
            }
            catch (InvalidOperationException)
            {
                window.Close();
                closed = true;
            }
        }

        if (closed)
        {
            _locking = true;
            Dispatcher.BeginInvoke(FinishLock, DispatcherPriority.Background);
            return;
        }

        FinishLock();
    }

    private void FinishLock()
    {
        _locking = false;
        if (!_session.TryLock())
            return;
        TunnelList.SelectedItem = null;
        LogBox.Clear();
        ShowGateError("");
        PassphraseBox.Clear();
        ConfirmBox.Clear();
        UpdateLogVisibility();
        FocusGate();
    }

    private async void OnGateClick(object sender, RoutedEventArgs e) => await SubmitGateAsync();

    private void OnGateKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        UpdateCapsLock();
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        _ = SubmitGateAsync();
    }

    private void OnGatePreviewKey(object sender, System.Windows.Input.KeyEventArgs e) => UpdateCapsLock();

    private void OnGateFocus(object sender, RoutedEventArgs e) => UpdateCapsLock();

    private async Task SubmitGateAsync()
    {
        if (_gateBusy || !_session.ShowGate)
            return;
        var passphrase = PassphraseBox.Password;
        var confirm = ConfirmBox.Password;
        var creating = _session.ShowConfirm;
        if (creating && !TunnelSession.TryValidateNewPassphrase(passphrase, confirm, out var invalid))
        {
            ShowGateError(invalid);
            return;
        }

        _gateBusy = true;
        SetGateEnabled(false);
        GateButtonText.Text = creating ? "Saving…" : "Checking…";
        _session.SetTransientStatus("Checking passphrase…");
        ShowGateError("");
        try
        {
            if (creating)
            {
                var key = await Task.Run(() => _session.CreateFile(passphrase, TunnelCipher.DefaultIterations)).ConfigureAwait(true);
                if (!IsLoaded)
                {
                    key?.Dispose();
                    _session.Dispose();
                    return;
                }

                if (key is null)
                {
                    ShowGateError(TunnelSession.CouldNotSave);
                    _session.PrepareCreate();
                    return;
                }

                _session.ApplyCreated(key);
            }
            else
            {
                var opened = await Task.Run(() => _session.OpenFile(passphrase)).ConfigureAwait(true);
                if (!IsLoaded)
                {
                    opened.Key?.Dispose();
                    _session.Dispose();
                    return;
                }

                if (opened.Status == TunnelOpenStatus.WrongPassphrase)
                {
                    opened.Key?.Dispose();
                    PassphraseBox.Clear();
                    ShowGateError(TunnelSession.WrongPassphrase);
                    _session.RestoreStatus();
                    FocusGate();
                    return;
                }

                if (opened.Status != TunnelOpenStatus.Ok || opened.Key is null)
                {
                    opened.Key?.Dispose();
                    ShowGateError(TunnelStore.CouldNotRead);
                    return;
                }

                _session.ApplyOpened(opened);
            }

            PassphraseBox.Clear();
            ConfirmBox.Clear();
            ShowGateError("");
            if (_session.ConsumeFreshUnlock())
                await _session.StartEnabledAsync().ConfigureAwait(true);
        }
        finally
        {
            _gateBusy = false;
            SetGateEnabled(true);
            if (_session.ShowGate)
                GateButtonText.Text = _session.ShowConfirm ? "Create session" : "Unlock";
        }
    }

    private void SetGateEnabled(bool enabled)
    {
        PassphraseBox.IsEnabled = enabled;
        ConfirmBox.IsEnabled = enabled;
        GateButton.IsEnabled = enabled;
    }

    private void ShowGateError(string message)
    {
        GateError.Text = message;
        GateError.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCapsLock()
    {
        var focused = PassphraseBox.IsKeyboardFocused || ConfirmBox.IsKeyboardFocused;
        CapsHint.Visibility = focused && Keyboard.IsKeyToggled(Key.CapsLock)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void FocusGate()
    {
        if (!_session.ShowGate)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_session.ShowGate)
                PassphraseBox.Focus();
        }, DispatcherPriority.Input);
    }

    private void UpdateLogVisibility()
    {
        LogCard.Visibility = _session.ShowSession && TunnelList.SelectedItem is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
