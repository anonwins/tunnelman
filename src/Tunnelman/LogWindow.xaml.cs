using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Threading;
using Tunnelman.Tunnels;

namespace Tunnelman;

internal partial class LogWindow : Window
{
    private TunnelItem? _item;
    private bool _stickToEnd = true;
    private bool _forceJump;

    public LogWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => TitleBarDarkMode.Apply(this);
        LogBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    public TunnelItem? Item => _item;

    public void ShowTunnel(TunnelItem item, bool jumpToEnd)
    {
        var changed = !ReferenceEquals(_item, item);
        if (changed)
        {
            if (_item is not null)
                _item.PropertyChanged -= OnItemChanged;
            _item = item;
            DataContext = item;
            item.PropertyChanged += OnItemChanged;
            _stickToEnd = true;
            _forceJump = jumpToEnd;
        }

        if (!IsVisible)
            Show();
        Activate();
        if (changed && jumpToEnd)
            Dispatcher.BeginInvoke(ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TunnelItem.LogText))
            return;
        var stick = _stickToEnd || _forceJump;
        Dispatcher.BeginInvoke(() =>
        {
            if (stick)
                ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_forceJump)
            return;
        var extent = e.ExtentHeight;
        var view = e.ViewportHeight;
        if (extent <= 0 || view <= 0)
        {
            _stickToEnd = true;
            return;
        }

        _stickToEnd = e.VerticalOffset + view >= extent - 4;
    }

    private void ScrollToEnd()
    {
        LogBox.CaretIndex = LogBox.Text.Length;
        LogBox.ScrollToEnd();
        _stickToEnd = true;
        _forceJump = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_item is not null)
            _item.PropertyChanged -= OnItemChanged;
        _item = null;
        base.OnClosed(e);
    }
}
