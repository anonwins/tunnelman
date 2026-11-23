using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Tunnelman.Tunnels;

namespace Tunnelman;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly TunnelSession _session;
    private readonly Action _show;
    private readonly Func<Task> _exit;
    private readonly ToolStripMenuItem _stateItem;
    private readonly string _version;
    private bool _disposed;

    public TrayController(TunnelSession session, Action show, Func<Task> exit)
    {
        _session = session;
        _show = show;
        _exit = exit;
        _version = VersionText();

        _stateItem = new ToolStripMenuItem(StateText()) { Enabled = false };
        var versionItem = new ToolStripMenuItem(_version) { Enabled = false };

        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
        };
        menu.Items.Add(_stateItem);
        menu.Items.Add(versionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open", null, (_, _) => _show()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => RunOnDispatcher(_exit)));

        _icon = new NotifyIcon
        {
            Visible = true,
            Text = TooltipText(),
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += OnMouseClick;
        _session.PropertyChanged += OnSessionPropertyChanged;
        UpdateMenu();
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            _show();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(TunnelSession.ShowSession) or null))
            return;

        void Update()
        {
            if (_disposed)
                return;
            UpdateMenu();
            _icon.Text = TooltipText();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Update();
        else
            dispatcher.BeginInvoke(Update);
    }

    private void UpdateMenu() => _stateItem.Text = StateText();

    private string StateText() => _session.ShowSession ? "Unlocked" : "Locked";

    private string TooltipText() => "Tunnelman - " + StateText();

    private static string VersionText()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "0.1.0" : version;
    }

    private static void RunOnDispatcher(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _ = action();
            return;
        }

        dispatcher.BeginInvoke(async () => await action().ConfigureAwait(true));
    }

    private static Icon LoadIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path)
            && !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                    return icon;
            }
            catch
            {
                // fall back
            }
        }

        var adjacent = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(adjacent))
        {
            try
            {
                return new Icon(adjacent);
            }
            catch
            {
                // fall back
            }
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _icon.MouseClick -= OnMouseClick;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
