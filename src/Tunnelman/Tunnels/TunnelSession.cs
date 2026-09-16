using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace Tunnelman.Tunnels;

internal sealed class TunnelSession : INotifyPropertyChanged, IDisposable
{
    public const string CouldNotSave = "Could not save the session.";
    public const string BakWarning = "Previous session file was unreadable and was copied to tunnels.json.bak.";

    private readonly TunnelStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Func<ITunnelKind, ITunnelRuntime>? _createRuntime;
    private bool _disposed;
    private bool _persistBlocked;
    private string _statusText = "Starting…";
    private string? _ncatPath;
    private bool _isNcatMissing = true;
    private string? _loadWarning;
    private string? _saveError;
    private string? _notice;

    public TunnelSession(
        TunnelStore? store = null,
        Dispatcher? dispatcher = null,
        Func<ITunnelKind, ITunnelRuntime>? createRuntime = null)
    {
        _store = store ?? new TunnelStore();
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _createRuntime = createRuntime;
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
            OnPropertyChanged(nameof(ShowNcatSetupHint));
            OnPropertyChanged(nameof(ToolsStatusText));
        }
    }

    public string ToolsStatusText => IsNcatMissing ? "Status: Setup needed" : "Status: OK";

    public bool HasTunnels => Tunnels.Count > 0;

    public bool ShowNoTunnelsHint => !HasTunnels && !IsNcatMissing;

    public bool ShowNcatSetupHint => IsNcatMissing && !HasTunnels;

    public int RunningCount => Tunnels.Count(t => t.Status == TunnelStatus.Running);

    public string RunningCountText =>
        RunningCount == 1 ? "1 running" : $"{RunningCount} running";

    public void Load()
    {
        try
        {
            TunnelStoreLoadResult loaded;
            try
            {
                loaded = _store.Load();
            }
            catch (Exception)
            {
                loaded = TunnelStoreLoadResult.FailUnreadable(TunnelStore.CouldNotRead);
            }

            Tunnels.Clear();
            foreach (var record in loaded.Tunnels)
                Tunnels.Add(CreateItem(record));

            if (loaded.Unreadable)
                _persistBlocked = true;
            else if (loaded.Error is null)
                _persistBlocked = false;

            _saveError = null;
            _notice = null;
            if (loaded.Corrupt && loaded.BakCopied)
                _loadWarning = BakWarning;
            else
                _loadWarning = loaded.Error;

            NotifyListFlags();
            RefreshSummary();
        }
        catch (Exception)
        {
            Tunnels.Clear();
            _persistBlocked = true;
            _saveError = null;
            _notice = null;
            _loadWarning = TunnelStore.CouldNotRead;
            NotifyListFlags();
            RefreshSummary();
        }
    }

    internal void SetNcatForTests(string? path, bool missing)
    {
        NcatPath = path;
        IsNcatMissing = missing;
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
        if (_disposed)
            return;

        var tasks = Tunnels.Where(t => t.Enabled && !t.IsBusy).Select(StartItemAsync);
        await Task.WhenAll(tasks).ConfigureAwait(true);
        if (_disposed)
            return;
        RefreshSummary();
    }

    public bool TryAdd(string name, string kind, JsonObject options, out string error)
    {
        error = "";
        if (_disposed)
            return false;

        var record = new TunnelRecord
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Kind = TunnelKindRegistry.Canonicalize(kind),
            Enabled = false,
            Options = options,
        };
        if (!TryValidate(record, ignoreId: null, out error))
            return false;

        var item = CreateItem(record);
        Tunnels.Add(item);
        if (!Persist())
        {
            Tunnels.Remove(item);
            error = CouldNotSave;
            NotifyListFlags();
            RefreshSummary();
            return false;
        }

        NotifyListFlags();
        RefreshSummary();
        return true;
    }

    public bool TryUpdate(TunnelItem item, string name, string kind, JsonObject options, out string error)
    {
        error = "";
        if (_disposed)
            return false;

        var next = item.Record.Clone();
        next.Name = name.Trim();
        next.Kind = TunnelKindRegistry.Canonicalize(kind);
        next.Options = options;
        if (!TryValidate(next, ignoreId: item.Id, out error))
            return false;

        var previousName = item.Record.Name;
        var previousKind = item.Record.Kind;
        var previousOptions = item.Record.Options is null ? null : (JsonObject)item.Record.Options.DeepClone();
        item.Record.Name = next.Name;
        item.Record.Kind = next.Kind;
        item.Record.Options = next.Options;
        item.RefreshIdentity();
        if (!Persist())
        {
            item.Record.Name = previousName;
            item.Record.Kind = previousKind;
            item.Record.Options = previousOptions;
            item.RefreshIdentity();
            error = CouldNotSave;
            RefreshSummary();
            return false;
        }

        RefreshSummary();
        return true;
    }

    public async Task ApplyEditAsync(TunnelItem item, string name, string kind, JsonObject options)
    {
        if (_disposed || item.IsBusy)
            return;
        if (!TryUpdate(item, name, kind, options, out var error))
            throw new InvalidOperationException(error);
        if (_disposed || !item.Enabled)
            return;
        await StopItemAsync(item).ConfigureAwait(true);
        if (_disposed)
            return;
        await StartItemAsync(item).ConfigureAwait(true);
        if (_disposed)
            return;
        RefreshSummary();
    }

    public async Task DeleteAsync(TunnelItem item)
    {
        if (_disposed || item.IsBusy)
            return;

        await StopItemAsync(item).ConfigureAwait(true);
        if (_disposed)
            return;
        if (!PersistRecords(Tunnels.Where(t => t != item).Select(t => t.Record)))
        {
            RefreshSummary();
            return;
        }

        Tunnels.Remove(item);
        NotifyListFlags();
        RefreshSummary();
    }

    public async Task SetEnabledAsync(TunnelItem item, bool enabled)
    {
        if (_disposed || item.IsBusy)
            return;
        if (enabled && !item.IsSupported)
            return;
        if (item.Enabled == enabled)
            return;

        var previous = item.Enabled;
        item.Enabled = enabled;
        if (!Persist())
        {
            item.Enabled = previous;
            RefreshSummary();
            return;
        }

        if (!item.IsSupported)
        {
            RefreshSummary();
            return;
        }

        if (enabled)
            await StartItemAsync(item).ConfigureAwait(true);
        else
            await StopItemAsync(item).ConfigureAwait(true);
        if (_disposed)
            return;
        RefreshSummary();
    }

    public async Task StopAllAsync()
    {
        if (_disposed)
            return;
        await Task.WhenAll(Tunnels.Select(StopItemAsync)).ConfigureAwait(true);
        if (_disposed)
            return;
        RefreshSummary();
    }

    public async Task<bool> ClearSessionAsync()
    {
        if (_disposed)
            return false;

        await StopAllAsync().ConfigureAwait(true);
        if (_disposed)
            return false;
        if (!PersistRecords([]))
        {
            RefreshSummary();
            return false;
        }

        Tunnels.Clear();
        _loadWarning = null;
        _saveError = null;
        _notice = null;
        NotifyListFlags();
        RefreshSummary();
        return true;
    }

    public IReadOnlyList<TunnelRecord> SnapshotRecords()
    {
        var copies = new List<TunnelRecord>(Tunnels.Count);
        foreach (var item in Tunnels)
        {
            var copy = item.Record.Clone();
            copy.Enabled = false;
            copies.Add(copy);
        }

        return copies;
    }

    public bool TryImport(IReadOnlyList<TunnelRecord> records, out int added, out string error)
    {
        added = 0;
        error = "";
        if (_disposed)
            return false;
        if (records.Count == 0)
            return true;

        var items = new List<TunnelItem>(records.Count);
        foreach (var incoming in records)
        {
            var record = incoming.Clone();
            record.Id = Guid.NewGuid();
            record.Enabled = false;
            record.Kind = TunnelKindRegistry.Canonicalize(record.Kind);
            items.Add(CreateItem(record));
        }

        foreach (var item in items)
            Tunnels.Add(item);

        if (!Persist())
        {
            foreach (var item in items)
                Tunnels.Remove(item);
            error = CouldNotSave;
            NotifyListFlags();
            RefreshSummary();
            return false;
        }

        added = items.Count;
        NotifyListFlags();
        SetNotice(TunnelTransfer.CountLabel("Imported", added));
        return true;
    }

    public void SetNotice(string text)
    {
        _notice = text;
        RefreshSummary();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var item in Tunnels)
        {
            var runtime = item.Runtime;
            if (runtime is null)
                continue;
            runtime.Dispose();
            item.SetRuntime(null);
        }
    }

    private bool Persist() => PersistRecords(Tunnels.Select(t => t.Record));

    private bool PersistRecords(IEnumerable<TunnelRecord> records)
    {
        if (_persistBlocked)
        {
            _saveError = CouldNotSave;
            return false;
        }

        try
        {
            _store.Save(records);
            _saveError = null;
            if (!_persistBlocked)
                _loadWarning = null;
            _notice = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveError = CouldNotSave;
            return false;
        }
    }

    private TunnelItem CreateItem(TunnelRecord record)
    {
        record.Kind = TunnelKindRegistry.Canonicalize(record.Kind);
        var item = new TunnelItem(record);
        if (!item.IsSupported)
            item.Mark(TunnelStatus.Unsupported);
        return item;
    }

    private async Task StartItemAsync(TunnelItem item)
    {
        if (_disposed || !item.Enabled)
            return;
        if (!item.IsSupported)
        {
            item.Mark(TunnelStatus.Unsupported);
            item.AppendLog("This tunnel type is not supported yet.");
            RefreshSummary();
            return;
        }

        if (IsNcatMissing || string.IsNullOrWhiteSpace(NcatPath))
        {
            item.Mark(TunnelStatus.Failed);
            item.AppendLog("Nmap ncat was not found.");
            RefreshSummary();
            return;
        }

        if (item.Runtime?.IsRunning == true)
            return;
        if (!item.TryBeginBusy(TunnelStatus.Starting))
            return;

        var previous = item.Runtime;
        if (previous is not null)
        {
            previous.Dispose();
            if (item.Runtime == previous)
                item.SetRuntime(null);
        }

        var kind = TunnelKindRegistry.Find(item.Kind);
        if (kind is null)
        {
            item.SetBusy(false);
            item.Mark(TunnelStatus.Unsupported);
            RefreshSummary();
            return;
        }

        if (_disposed)
        {
            item.SetBusy(false);
            return;
        }

        var runtime = _createRuntime?.Invoke(kind) ?? kind.CreateRuntime();
        void OnExit(int code) => OnRuntimeExited(item, runtime, code);
        runtime.Exited += OnExit;
        item.SetRuntime(runtime);
        try
        {
            await runtime.StartAsync(item.Record, NcatPath!, line => PostLog(item, line), CancellationToken.None).ConfigureAwait(true);
            if (_disposed || item.Runtime != runtime)
            {
                runtime.Exited -= OnExit;
                runtime.Dispose();
                if (item.Runtime == runtime)
                    item.SetRuntime(null);
                item.SetBusy(false);
                return;
            }

            if (runtime.IsRunning)
                item.SetBusy(false, TunnelStatus.Running);
            else
                Fail(item, runtime, "ncat is not running.");
        }
        catch (Exception ex)
        {
            if (_disposed)
            {
                runtime.Exited -= OnExit;
                runtime.Dispose();
                if (item.Runtime == runtime)
                    item.SetRuntime(null);
                item.SetBusy(false);
                return;
            }

            Fail(item, runtime, ex.Message);
        }

        if (!_disposed)
            RefreshSummary();
    }

    private async Task StopItemAsync(TunnelItem item)
    {
        if (_disposed)
            return;

        var runtime = item.Runtime;
        if (runtime is null)
        {
            MarkIdle(item, unexpected: false);
            return;
        }

        if (!item.TryBeginBusy(TunnelStatus.Stopping) && item.Status is not TunnelStatus.Stopping)
            return;

        try
        {
            await runtime.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!_disposed)
                item.AppendLog(ex.Message);
        }
        finally
        {
            if (item.Runtime == runtime)
            {
                runtime.Dispose();
                item.SetRuntime(null);
            }

            if (!_disposed)
            {
                item.SetBusy(false);
                MarkIdle(item, unexpected: false);
            }
        }
    }

    private void PostLog(TunnelItem item, string line)
    {
        if (_disposed)
            return;
        if (_dispatcher.CheckAccess())
            item.AppendLog(line);
        else
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                    item.AppendLog(line);
            });
    }

    private void OnRuntimeExited(TunnelItem item, ITunnelRuntime runtime, int code)
    {
        void Apply()
        {
            if (_disposed || item.Runtime != runtime)
                return;
            if (item.Status is TunnelStatus.Stopping)
                return;
            item.AppendLog($"Process ended ({code}).");
            runtime.Dispose();
            if (item.Runtime == runtime)
                item.SetRuntime(null);
            MarkIdle(item, unexpected: true);
            _notice = null;
            RefreshSummary();
        }

        if (_dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.BeginInvoke(Apply);
    }

    private void Fail(TunnelItem item, ITunnelRuntime runtime, string message)
    {
        if (_disposed)
            return;
        item.AppendLog(message);
        runtime.Dispose();
        if (item.Runtime == runtime)
            item.SetRuntime(null);
        item.Mark(TunnelStatus.Failed);
        _notice = null;
    }

    private static void MarkIdle(TunnelItem item, bool unexpected)
    {
        if (!item.IsSupported)
        {
            item.Mark(TunnelStatus.Unsupported);
            return;
        }

        item.Mark(unexpected ? TunnelStatus.Failed : TunnelStatus.Stopped);
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

        record.Kind = TunnelKindRegistry.Canonicalize(record.Kind);
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

        if (TunnelKindRegistry.Is(record.Kind, TunnelKinds.NcatSocks)
            && NcatSocksOptions.TryParse(record.Options, out var options, out _))
        {
            var occupied = new List<(string Host, int Port)>();
            foreach (var other in Tunnels)
            {
                if (other.Id == ignoreId || !TunnelKindRegistry.Is(other.Kind, TunnelKinds.NcatSocks))
                    continue;
                if (!NcatSocksOptions.TryParse(other.Record.Options, out var existing, out _))
                    continue;
                occupied.Add((existing.ListenAddress, existing.ListenPort));
            }

            if (ListenOccupancy.ConflictsWithAny(options.ListenAddress, options.ListenPort, occupied))
            {
                error = "Another tunnel already uses this listen address and port.";
                return false;
            }
        }

        error = "";
        return true;
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(RunningCountText));
        if (!string.IsNullOrWhiteSpace(_saveError))
        {
            StatusText = _saveError;
            return;
        }

        var live = Tunnels.Any(t => t.Status is TunnelStatus.Running or TunnelStatus.Failed);
        if (live)
        {
            StatusText = $"{RunningCount} of {Tunnels.Count} running";
            return;
        }

        if (!string.IsNullOrWhiteSpace(_notice))
        {
            StatusText = _notice;
            return;
        }

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

    private void NotifyListFlags()
    {
        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(ShowNoTunnelsHint));
        OnPropertyChanged(nameof(ShowNcatSetupHint));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
