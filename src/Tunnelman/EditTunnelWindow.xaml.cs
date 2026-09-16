using System.Text.Json.Nodes;
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
        FillKinds(existing?.Kind);
        var options = NcatSocksOptions.Defaults();
        if (existing is not null)
        {
            TitleText.Text = "Edit tunnel";
            NameBox.Text = existing.Name;
            if (TunnelKindRegistry.Is(existing.Kind, TunnelKinds.NcatSocks)
                && NcatSocksOptions.TryParse(existing.Record.Options, out var parsed, out _))
                options = parsed;
        }

        RemoteHostBox.Text = options.RemoteHost;
        RemotePortBox.Text = options.RemotePort.ToString();
        ListenAddressBox.Text = options.ListenAddress;
        ListenPortBox.Text = options.ListenPort.ToString();
        SocksHostBox.Text = options.SocksHost;
        SocksPortBox.Text = options.SocksPort.ToString();
        NameBox.Focus();
    }

    public string TunnelName { get; private set; } = "";
    public string Kind { get; private set; } = TunnelKinds.NcatSocks;
    public JsonObject? Options { get; private set; }
    private bool _committing;

    private void FillKinds(string? selectedKind)
    {
        KindBox.Items.Clear();
        ComboBoxItem? selected = null;
        foreach (var kind in TunnelKindRegistry.All)
        {
            var item = new ComboBoxItem { Content = kind.DisplayName, Tag = kind.Kind };
            KindBox.Items.Add(item);
            if (kind.Kind == selectedKind)
                selected = item;
        }

        KindBox.SelectedItem = selected ?? KindBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => TitleBarDarkMode.Apply(this);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || !DialogCommit.TryBegin(button, ref _committing))
            return;
        try
        {
            var options = new NcatSocksOptions
            {
                RemoteHost = RemoteHostBox.Text.Trim(),
                ListenAddress = ListenAddressBox.Text.Trim(),
                SocksHost = SocksHostBox.Text.Trim(),
            };

            if (!int.TryParse(RemotePortBox.Text.Trim(), out var remotePort)
                || !int.TryParse(ListenPortBox.Text.Trim(), out var listenPort)
                || !int.TryParse(SocksPortBox.Text.Trim(), out var socksPort))
            {
                MessageBox.Show(this, "Ports must be numbers.", "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            options.RemotePort = remotePort;
            options.ListenPort = listenPort;
            options.SocksPort = socksPort;
            if (!NcatCommandBuilder.TryValidate(options, out var error))
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
            Kind = TunnelKinds.NcatSocks;
            Options = options.ToJson();
            DialogResult = true;
        }
        finally
        {
            DialogCommit.End(button, () => _committing = false);
        }
    }
}
