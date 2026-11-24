using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
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
        var adjacent = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (!File.Exists(adjacent))
            return SystemIcons.Application;

        try
        {
            using var sourceIcon = new Icon(adjacent);
            using var source = sourceIcon.ToBitmap();
            var size = SystemInformation.SmallIconSize;
            using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height));
            }

            var handle = bitmap.GetHicon();
            try
            {
                using var icon = Icon.FromHandle(handle);
                return (Icon)icon.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

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
