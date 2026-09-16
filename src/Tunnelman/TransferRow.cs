using System.ComponentModel;
using System.Runtime.CompilerServices;
using Tunnelman.Tunnels;

namespace Tunnelman;

public sealed class TransferRow : INotifyPropertyChanged
{
    private bool _isChecked;

    internal TransferRow(TransferEntry entry)
    {
        Entry = entry;
        _isChecked = entry.CanSelect;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal TransferEntry Entry { get; }

    public string DisplayName => Entry.DisplayName;
    public string ListenText => Entry.ListenText;
    public string RemoteText => Entry.RemoteText;
    public string? Note => Entry.Note;
    public bool CanSelect => Entry.CanSelect;
    public bool ShowEndpoints => Entry.ShowEndpoints;
    public bool HasNote => Entry.HasNote;
    public double RowOpacity => CanSelect ? 1 : 0.45;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!CanSelect)
                value = false;
            if (_isChecked == value)
                return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
