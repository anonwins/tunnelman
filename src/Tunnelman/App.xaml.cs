using System.Windows;
using Microsoft.Win32;
using Tunnelman.Tunnels;

namespace Tunnelman;

public partial class App : System.Windows.Application
{
    private SingleInstance? _single;
    private TunnelSession? _session;
    private MainWindow? _main;
    private TrayController? _tray;
    private bool _watchingAccent;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        WindowsAccent.Apply(Resources);

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        if (!SingleInstance.TryStart(out var single) || single is null)
        {
            Shutdown();
            return;
        }

        _single = single;
        if (!AppPaths.TryEnsureDirectory())
        {
            MessageBox.Show(
                "Could not protect application data. The app will not start.",
                "Tunnelman",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _watchingAccent = true;
        UiSettings.Load();
        var store = new TunnelStore();
        var probe = store.Probe();
        if (probe.Kind == TunnelProbeKind.Foreign)
        {
            MessageBox.Show(
                TunnelStore.ForeignFile,
                "Tunnelman",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        if (probe.Kind == TunnelProbeKind.Unreadable)
        {
            MessageBox.Show(
                TunnelStore.CouldNotRead,
                "Tunnelman",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _session = new TunnelSession(store);
        var loaded = _session.Load();
        if (loaded.Status is TunnelLoadStatus.Foreign or TunnelLoadStatus.Unreadable)
        {
            MessageBox.Show(
                loaded.Status == TunnelLoadStatus.Unreadable ? TunnelStore.CouldNotRead : TunnelStore.ForeignFile,
                "Tunnelman",
                MessageBoxButton.OK,
                loaded.Status == TunnelLoadStatus.Unreadable ? MessageBoxImage.Error : MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _main = new MainWindow(_session);
        var session = _session;
        _main.Loaded += async (_, _) => await session.StartEnabledAsync();
        _tray = new TrayController(
            () => _main.RestoreFromTray(),
            () => _main.RequestExitAsync());
        _single.ShowRequested += () => Dispatcher.BeginInvoke(() => _main.RestoreFromTray());
        _main.Show();
    }

    private void App_OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _main?.AllowCloseForShutdown();
        _session?.Dispose();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Color or UserPreferenceCategory.General))
            return;
        Dispatcher.BeginInvoke(() => WindowsAccent.Apply(Resources));
    }

    private void App_OnExit(object sender, ExitEventArgs e)
    {
        if (_watchingAccent)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _watchingAccent = false;
        }

        _session?.Dispose();
        _tray?.Dispose();
        _single?.Dispose();
    }
}
