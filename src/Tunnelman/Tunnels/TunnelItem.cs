using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tunnelman.Tunnels;

internal sealed class TunnelItem : INotifyPropertyChanged
{
    public const int MaxLogLines = 2000;

    private readonly List<string> _lines = [];
    private readonly StringBuilder _log = new();
    private TunnelStatus _status = TunnelStatus.Stopped;
    private bool _isBusy;
    private ITunnelRuntime? _runtime;

    public TunnelItem(TunnelRecord record)
    {
        Record = record;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TunnelRecord Record { get; }

    public Guid Id => Record.Id;

    public string Name => Record.Name;

    public bool Enabled
    {
        get => Record.Enabled;
        set
        {
            if (Record.Enabled == value)
                return;
            Record.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanToggle));
        }
    }

    public string ListenText => Record.ListenText;
    public string RemoteText => Record.RemoteText;
    public string ProxyText => Record.ProxyText;
    public bool HasProxy => Record.HasProxy;
    public bool IsDirect => !HasProxy;

    public TunnelStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => Status switch
    {
        TunnelStatus.Starting => "Starting",
        TunnelStatus.Running => "Running",
        TunnelStatus.Stopping => "Stopping",
        TunnelStatus.Failed => "Failed",
        _ => "Stopped",
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(CanMutate));
        }
    }

    public bool CanToggle => !IsBusy;

    public bool CanMutate => !IsBusy;

    public string LogText
    {
        get
        {
            lock (_lines)
                return _log.ToString();
        }
    }

    internal ITunnelRuntime? Runtime => _runtime;

    public void RefreshIdentity()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ListenText));
        OnPropertyChanged(nameof(RemoteText));
        OnPropertyChanged(nameof(ProxyText));
        OnPropertyChanged(nameof(HasProxy));
        OnPropertyChanged(nameof(IsDirect));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanMutate));
        OnPropertyChanged(nameof(Enabled));
    }

    public void SetBusy(bool busy, TunnelStatus? status = null)
    {
        IsBusy = busy;
        if (status is { } next)
            Status = next;
    }

    public bool TryBeginBusy(TunnelStatus status)
    {
        if (_isBusy)
            return false;
        SetBusy(true, status);
        return true;
    }

    public void SetRuntime(ITunnelRuntime? runtime) => _runtime = runtime;

    public void Mark(TunnelStatus status)
    {
        Status = status;
        if (status is TunnelStatus.Stopped or TunnelStatus.Failed)
            IsBusy = false;
    }

    public void ClearLog()
    {
        lock (_lines)
        {
            _lines.Clear();
            _log.Clear();
        }

        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(HasLog));
    }

    public bool HasLog
    {
        get
        {
            lock (_lines)
                return _lines.Count > 0;
        }
    }

    public void AppendLog(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
            while (_lines.Count > MaxLogLines)
                _lines.RemoveAt(0);
            _log.Clear();
            _log.AppendJoin(Environment.NewLine, _lines);
        }

        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(HasLog));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
