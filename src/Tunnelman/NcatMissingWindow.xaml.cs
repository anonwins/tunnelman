using System.Windows;
using Microsoft.Win32;

namespace Tunnelman;

public partial class NcatMissingWindow : Window
{
    public NcatMissingWindow()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => TitleBarDarkMode.Apply(this);

    private void RefreshStatus()
    {
        var result = NcatResolver.Resolve();
        if (result.Found)
        {
            HeadingText.Text = "Nmap ncat is ready";
            BodyText.Text = "Tunnelman is using this ncat. Close to keep it, or pick a different ncat.exe.";
            StatusText.Text = result.Path;
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
            BrowseLabel.Text = "Change ncat.exe";
            InstallButton.Visibility = Visibility.Collapsed;
            CloseButton.Style = (Style)FindResource("PrimaryButton");
            CloseButton.IsDefault = true;
            return;
        }

        HeadingText.Text = "Nmap ncat is required";
        BodyText.Text = "SOCKS5 hops need Nmap’s ncat. Install Nmap (the official Windows installer, not a standalone zip) or browse to ncat.exe. Installing may ask for administrator permission.";
        StatusText.Text = result.Error ?? "Nmap ncat was not found.";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        BrowseLabel.Text = "Browse for ncat.exe";
        InstallButton.Visibility = Visibility.Visible;
        InstallButton.IsEnabled = true;
        CloseButton.ClearValue(StyleProperty);
        CloseButton.IsDefault = false;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ncat.exe",
            Filter = "ncat.exe|ncat.exe|Executables (*.exe)|*.exe",
            FileName = "ncat.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var probe = NcatResolver.LooksLikeNmapNcat(NcatResolver.TryReadVersion(dialog.FileName) ?? "");
        if (!probe)
        {
            MessageBox.Show(
                this,
                "That file does not look like Nmap ncat.",
                "Tunnelman",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        UiSettings.Current.RememberNcatPath(dialog.FileName);
        RefreshStatus();
        DialogResult = true;
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (!NcatInstaller.TryFindWinget(out _))
        {
            NcatInstaller.OpenNmapDownloadPage();
            StatusText.Text = "winget was not found. The Nmap download page was opened. Install Nmap, then browse to ncat.exe.";
            return;
        }

        InstallButton.IsEnabled = false;
        StatusText.Text = "Installing Nmap with winget…";
        try
        {
            var (ok, output) = await NcatInstaller.InstallNmapAsync(CancellationToken.None).ConfigureAwait(true);
            if (!ok)
            {
                StatusText.Text = string.IsNullOrWhiteSpace(output)
                    ? "Nmap install failed. You can install it from nmap.org and browse to ncat.exe."
                    : output;
                NcatInstaller.OpenNmapDownloadPage();
                return;
            }

            RefreshStatus();
            var result = NcatResolver.Resolve();
            if (result.Found)
                DialogResult = true;
            else
                StatusText.Text = "Nmap finished installing, but ncat was not found yet. Browse to ncat.exe if needed.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }
}
