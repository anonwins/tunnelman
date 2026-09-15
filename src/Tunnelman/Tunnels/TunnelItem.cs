using System.Collections.Generic;
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

    public string Kind => Record.Kind;

    public bool Enabled
    {
        get => Record.Enabled;
        set
        {
            if (Record.Enabled == value)
                return;
            Record.Enabled = value;
            OnPropertyChanged();
        }
    }

    public bool IsSupported => TunnelKindRegistry.Find(Kind) is not null;

    public string EndpointText => TryOptions()?.EndpointText ?? Kind;
    public string ListenText => TryOptions()?.ListenText ?? "";
    public string RemoteText => TryOptions()?.RemoteText ?? "";
    public string ProxyText => TryOptions()?.ProxyText ?? "";
    public bool HasProxy => TryOptions()?.HasProxy == true;

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
        TunnelStatus.Unsupported => "Unsupported",
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
        }
    }

    public bool CanToggle => !IsBusy && IsSupported;

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
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(EndpointText));
        OnPropertyChanged(nameof(ListenText));
        OnPropertyChanged(nameof(RemoteText));
        OnPropertyChanged(nameof(ProxyText));
        OnPropertyChanged(nameof(HasProxy));
        OnPropertyChanged(nameof(IsSupported));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(Enabled));
    }

    public void SetBusy(bool busy, TunnelStatus? status = null)
    {
        IsBusy = busy;
        if (status is { } next)
            Status = next;
    }

    public void SetRuntime(ITunnelRuntime? runtime)
    {
        _runtime = runtime;
        if (runtime is null && Status is TunnelStatus.Starting or TunnelStatus.Running or TunnelStatus.Stopping)
            Status = Enabled ? TunnelStatus.Failed : TunnelStatus.Stopped;
    }

    public void Mark(TunnelStatus status)
    {
        Status = status;
        if (status is TunnelStatus.Stopped or TunnelStatus.Failed or TunnelStatus.Unsupported)
            IsBusy = false;
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
    }

    private NcatSocksOptions? TryOptions() =>
        Kind == TunnelKinds.NcatSocks
        && NcatSocksOptions.TryParse(Record.Options, out var options, out _)
            ? options
            : null;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
