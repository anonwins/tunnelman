using System.Windows;
using System.Windows.Input;
using Tunnelman.Tunnels;

namespace Tunnelman;

public partial class ExportUnlockWindow : Window
{
    private readonly byte[] _salt;
    private readonly int _iterations;
    private readonly Func<SessionKey, ExportReadResult> _read;
    private bool _busy;

    internal IReadOnlyList<TunnelRecord> Records { get; private set; } = [];
    internal string? Failure { get; private set; }

    internal ExportUnlockWindow(byte[] salt, int iterations, Func<SessionKey, ExportReadResult> read)
    {
        InitializeComponent();
        _salt = salt;
        _iterations = iterations;
        _read = read;
        Loaded += (_, _) => PassphraseBox.Focus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => TitleBarDarkMode.Apply(this);

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        UpdateCapsLock();
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        _ = SubmitAsync();
    }

    private void OnPreviewKey(object sender, System.Windows.Input.KeyEventArgs e) => UpdateCapsLock();

    private void OnFocus(object sender, RoutedEventArgs e) => UpdateCapsLock();

    private async void OnUnlockClick(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async Task SubmitAsync()
    {
        if (_busy)
            return;
        var passphrase = PassphraseBox.Password;
        _busy = true;
        PassphraseBox.IsEnabled = false;
        UnlockButton.IsEnabled = false;
        UnlockText.Text = "Checking…";
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var result = await Task.Run(() =>
            {
                var key = SessionKey.Derive(passphrase, _salt, _iterations);
                try
                {
                    return _read(key);
                }
                finally
                {
                    key.Dispose();
                }
            }).ConfigureAwait(true);

            if (!IsLoaded)
                return;
            if (result.Status == ExportReadStatus.Ok)
            {
                Records = result.Tunnels;
                DialogResult = true;
                return;
            }

            if (result.Status == ExportReadStatus.WrongPassphrase)
            {
                PassphraseBox.Clear();
                ErrorText.Text = TunnelSession.WrongPassphrase;
                ErrorText.Visibility = Visibility.Visible;
                PassphraseBox.Focus();
                return;
            }

            Failure = result.Error;
            DialogResult = false;
        }
        finally
        {
            _busy = false;
            if (IsLoaded && DialogResult is not true)
            {
                PassphraseBox.IsEnabled = true;
                UnlockButton.IsEnabled = true;
                UnlockText.Text = "Unlock";
            }
        }
    }

    private void UpdateCapsLock()
    {
        var show = PassphraseBox.IsKeyboardFocused && Keyboard.IsKeyToggled(Key.CapsLock);
        CapsHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
}
