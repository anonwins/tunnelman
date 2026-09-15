using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace Tunnelman.Tunnels;

internal sealed class TunnelSession : INotifyPropertyChanged, IDisposable
{
    private readonly TunnelStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private bool _disposed;
    private string _statusText = "Starting…";
    private string? _ncatPath;
    private bool _isNcatMissing = true;
    private string? _loadWarning;

    public TunnelSession(TunnelStore? store = null, Dispatcher? dispatcher = null)
    {
        _store = store ?? new TunnelStore();
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TunnelItem> Tunnels { get; } = [];

    public string WindowTitle => "Tunnelman";

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
                return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string? NcatPath
    {
        get => _ncatPath;
        private set
        {
            if (_ncatPath == value)
                return;
            _ncatPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NcatPathText));
        }
    }

    public string NcatPathText =>
        string.IsNullOrWhiteSpace(NcatPath) ? "ncat not found" : NcatPath;

    public bool IsNcatMissing
    {
        get => _isNcatMissing;
        private set
        {
            if (_isNcatMissing == value)
                return;
            _isNcatMissing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowNoTunnelsHint));
            OnPropertyChanged(nameof(ShowNcatMissing));
            OnPropertyChanged(nameof(ToolsReady));
            OnPropertyChanged(nameof(ToolsStatusText));
        }
    }

    public bool ToolsReady => !IsNcatMissing;

    public string ToolsStatusText => ToolsReady ? "Status: OK" : "Status: Setup needed";

    public bool HasTunnels => Tunnels.Count > 0;

    public bool ShowNoTunnelsHint => !HasTunnels && !IsNcatMissing;

    public bool ShowNcatMissing => IsNcatMissing;

    public int RunningCount => Tunnels.Count(t => t.Status == TunnelStatus.Running);

    public string RunningCountText =>
        RunningCount == 1 ? "1 running" : $"{RunningCount} running";

    public void Load()
    {
        var loaded = _store.Load();
        Tunnels.Clear();
        foreach (var record in loaded.Tunnels)
            Tunnels.Add(CreateItem(record));

        _loadWarning = loaded.Corrupt
            ? "Previous session file was unreadable and was copied to tunnels.json.bak."
            : loaded.Error;
        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(ShowNoTunnelsHint));
        RefreshSummary();
    }

    public void ResolveNcat()
    {
        var result = NcatResolver.Resolve();
        NcatPath = result.Path;
        IsNcatMissing = !result.Found;
        RefreshSummary();
    }

    public async Task StartEnabledAsync()
    {
        if (IsNcatMissing)
        {
            RefreshSummary();
            return;
        }

        var tasks = Tunnels.Where(t => t.Enabled).Select(StartItemAsync);
        await Task.WhenAll(tasks).ConfigureAwait(true);
        RefreshSummary();
    }

    public bool TryAdd(string name, string kind, JsonObject options, out string error)
    {
        var record = new TunnelRecord
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Kind = kind,
            Enabled = false,
            Options = options,
        };
        if (!TryValidate(record, ignoreId: null, out error))
            return false;

        Tunnels.Add(CreateItem(record));
        Persist();
        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(ShowNoTunnelsHint));
        RefreshSummary();
        return true;
    }

    public bool TryUpdate(TunnelItem item, string name, string kind, JsonObject options, out string error)
    {
        var next = item.Record.Clone();
        next.Name = name.Trim();
        next.Kind = kind;
        next.Options = options;
        if (!TryValidate(next, ignoreId: item.Id, out error))
            return false;

        item.Record.Name = next.Name;
        item.Record.Kind = next.Kind;
        item.Record.Options = next.Options;
        item.RefreshIdentity();
        Persist();
        return true;
    }

    public async Task ApplyEditAsync(TunnelItem item, string name, string kind, JsonObject options)
    {
        if (!TryUpdate(item, name, kind, options, out var error))
            throw new InvalidOperationException(error);
        if (!item.Enabled)
            return;
        await StopItemAsync(item).ConfigureAwait(true);
        await StartItemAsync(item).ConfigureAwait(true);
    }

    public async Task DeleteAsync(TunnelItem item)
    {
        await StopItemAsync(item).ConfigureAwait(true);
        Tunnels.Remove(item);
        Persist();
        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(ShowNoTunnelsHint));
        RefreshSummary();
    }

    public async Task SetEnabledAsync(TunnelItem item, bool enabled)
    {
        if (item.IsBusy || !item.IsSupported)
            return;

        item.Enabled = enabled;
        Persist();
        if (enabled)
            await StartItemAsync(item).ConfigureAwait(true);
        else
            await StopItemAsync(item).ConfigureAwait(true);
        RefreshSummary();
    }

    public async Task StopAllAsync()
    {
        await Task.WhenAll(Tunnels.Select(StopItemAsync)).ConfigureAwait(true);
        RefreshSummary();
    }

    public async Task ClearSessionAsync()
    {
        await StopAllAsync().ConfigureAwait(true);
        Tunnels.Clear();
        _store.Clear();
        _loadWarning = null;
        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(ShowNoTunnelsHint));
        RefreshSummary();
    }

    public void Persist()
    {
        try
        {
            _store.Save(Tunnels.Select(t => t.Record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "Could not save the session.";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var item in Tunnels)
            item.Runtime?.Dispose();
    }

    private TunnelItem CreateItem(TunnelRecord record)
    {
        var item = new TunnelItem(record);
        if (!item.IsSupported)
            item.Mark(TunnelStatus.Unsupported);
        return item;
    }

    private async Task StartItemAsync(TunnelItem item)
    {
        if (!item.Enabled)
            return;
        if (!item.IsSupported)
        {
            item.Mark(TunnelStatus.Unsupported);
            item.AppendLog("This tunnel type is not supported yet.");
            return;
        }

        if (IsNcatMissing || string.IsNullOrWhiteSpace(NcatPath))
        {
            item.Mark(TunnelStatus.Failed);
            item.AppendLog("Nmap ncat was not found.");
            return;
        }

        if (item.Runtime?.IsRunning == true)
            return;

        item.Runtime?.Dispose();
        item.SetRuntime(null);

        var kind = TunnelKindRegistry.Find(item.Kind);
        if (kind is null)
        {
            item.Mark(TunnelStatus.Unsupported);
            return;
        }

        item.SetBusy(true, TunnelStatus.Starting);
        var runtime = kind.CreateRuntime();
        runtime.Exited += code => OnRuntimeExited(item, code);
        item.SetRuntime(runtime);
        try
        {
            await runtime.StartAsync(item.Record, NcatPath!, line => PostLog(item, line), CancellationToken.None).ConfigureAwait(true);
            if (runtime.IsRunning)
                item.SetBusy(false, TunnelStatus.Running);
            else
                Fail(item, "ncat is not running.");
        }
        catch (Exception ex)
        {
            Fail(item, ex.Message);
        }
    }

    private async Task StopItemAsync(TunnelItem item)
    {
        var runtime = item.Runtime;
        if (runtime is null)
        {
            item.Mark(item.Enabled ? item.Status : TunnelStatus.Stopped);
            if (!item.Enabled)
                item.Mark(TunnelStatus.Stopped);
            return;
        }

        item.SetBusy(true, TunnelStatus.Stopping);
        try
        {
            await runtime.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            item.AppendLog(ex.Message);
        }
        finally
        {
            runtime.Dispose();
            item.SetRuntime(null);
            item.SetBusy(false, item.Enabled ? TunnelStatus.Failed : TunnelStatus.Stopped);
            if (!item.Enabled)
                item.Mark(TunnelStatus.Stopped);
        }
    }

    private void PostLog(TunnelItem item, string line)
    {
        if (_dispatcher.CheckAccess())
            item.AppendLog(line);
        else
            _dispatcher.BeginInvoke(() => item.AppendLog(line));
    }

    private void OnRuntimeExited(TunnelItem item, int code)
    {
        void Apply()
        {
            if (item.Status is TunnelStatus.Stopping)
                return;
            item.AppendLog($"Process ended ({code}).");
            item.SetRuntime(null);
            item.Mark(item.Enabled ? TunnelStatus.Failed : TunnelStatus.Stopped);
            RefreshSummary();
        }

        if (_dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.BeginInvoke(Apply);
    }

    private static void Fail(TunnelItem item, string message)
    {
        item.AppendLog(message);
        item.Runtime?.Dispose();
        item.SetRuntime(null);
        item.Mark(TunnelStatus.Failed);
    }

    internal bool TryValidate(TunnelRecord record, Guid? ignoreId, out string error)
    {
        if (string.IsNullOrWhiteSpace(record.Name))
        {
            error = "Name is required.";
            return false;
        }

        if (Tunnels.Any(t => t.Id != ignoreId && t.Name.Equals(record.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            error = "A tunnel with this name already exists.";
            return false;
        }

        var kind = TunnelKindRegistry.Find(record.Kind);
        if (kind is null)
        {
            error = "Unknown tunnel type.";
            return false;
        }

        if (record.Options is null)
        {
            error = "Tunnel options are invalid.";
            return false;
        }

        if (!kind.TryValidate(record.Options, out error))
            return false;

        if (record.Kind == TunnelKinds.NcatSocks
            && NcatSocksOptions.TryParse(record.Options, out var options, out _))
        {
            var key = $"{options.ListenAddress.Trim()}:{options.ListenPort}";
            foreach (var other in Tunnels)
            {
                if (other.Id == ignoreId || other.Kind != TunnelKinds.NcatSocks)
                    continue;
                if (!NcatSocksOptions.TryParse(other.Record.Options, out var existing, out _))
                    continue;
                if ($"{existing.ListenAddress.Trim()}:{existing.ListenPort}"
                    .Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Another tunnel already uses this listen address and port.";
                    return false;
                }
            }
        }

        error = "";
        return true;
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(RunningCountText));
        if (!string.IsNullOrWhiteSpace(_loadWarning))
        {
            StatusText = _loadWarning;
            return;
        }

        if (IsNcatMissing)
        {
            StatusText = "Nmap ncat was not found.";
            return;
        }

        StatusText = $"{RunningCount} of {Tunnels.Count} running";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
