using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Tunnelman;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Action _show;
    private readonly Func<Task> _exit;
    private readonly string _version;
    private bool _disposed;

    public TrayController(Action show, Func<Task> exit)
    {
        _show = show;
        _exit = exit;
        _version = VersionText();

        var versionItem = new ToolStripMenuItem(_version) { Enabled = false };

        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
        };
        menu.Items.Add(versionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open", null, (_, _) => _show()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => RunOnDispatcher(_exit)));

        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "Tunnelman",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += OnMouseClick;
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            _show();
    }

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
        _icon.MouseClick -= OnMouseClick;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
