using System.Windows;
using System.Windows.Controls;
using Tunnelman.Tunnels;

namespace Tunnelman;

public partial class EditTunnelWindow : Window
{
    public EditTunnelWindow() : this(null)
    {
    }

    internal EditTunnelWindow(TunnelItem? existing)
    {
        InitializeComponent();
        var record = existing?.Record.Clone() ?? TunnelRecord.Defaults();
        TitleText.Text = existing is null ? "New tunnel" : "Edit tunnel";
        if (existing is not null)
            NameBox.Text = existing.Name;
        RemoteHostBox.Text = record.RemoteHost;
        RemotePortBox.Text = record.RemotePort.ToString();
        ListenAddressBox.Text = record.ListenAddress;
        ListenPortBox.Text = record.ListenPort.ToString();
        SocksHostBox.Text = record.SocksHost;
        SocksPortBox.Text = record.SocksPort.ToString();
        NameBox.Focus();
    }

    public string TunnelName { get; private set; } = "";
    internal TunnelRecord? Draft { get; private set; }
    private bool _committing;

    private void OnSourceInitialized(object? sender, EventArgs e) => TitleBarDarkMode.Apply(this);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || !DialogCommit.TryBegin(button, ref _committing))
            return;
        try
        {
            if (!int.TryParse(RemotePortBox.Text.Trim(), out var remotePort)
                || !int.TryParse(ListenPortBox.Text.Trim(), out var listenPort)
                || !int.TryParse(SocksPortBox.Text.Trim(), out var socksPort))
            {
                MessageBox.Show(this, "Ports must be numbers.", "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var draft = new TunnelRecord
            {
                RemoteHost = RemoteHostBox.Text.Trim(),
                RemotePort = remotePort,
                ListenAddress = ListenAddressBox.Text.Trim(),
                ListenPort = listenPort,
                SocksHost = SocksHostBox.Text.Trim(),
                SocksPort = socksPort,
            };
            if (!draft.TryValidateEndpoints(out var error))
            {
                MessageBox.Show(this, error, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                MessageBox.Show(this, "Name is required.", "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TunnelName = NameBox.Text.Trim();
            draft.Name = TunnelName;
            Draft = draft;
            DialogResult = true;
        }
        finally
        {
            DialogCommit.End(button, () => _committing = false);
        }
    }
}
