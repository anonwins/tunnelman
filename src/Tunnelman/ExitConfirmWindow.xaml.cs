using System.Windows;

namespace Tunnelman;

public partial class ExitConfirmWindow : Window
{
    public bool ClearSession { get; private set; }

    public ExitConfirmWindow(bool allowClear = true)
    {
        InitializeComponent();
        if (allowClear)
            return;
        ClearButton.Visibility = Visibility.Collapsed;
        BodyText.Text = "Running tunnels will stop. The saved list stays.";
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => TitleBarDarkMode.Apply(this);

    private void OnKeepClick(object sender, RoutedEventArgs e)
    {
        ClearSession = false;
        DialogResult = true;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ClearSession = true;
        DialogResult = true;
    }
}
