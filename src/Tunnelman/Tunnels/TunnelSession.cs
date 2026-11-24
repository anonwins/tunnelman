using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Tunnelman.Tunnels;

internal sealed class TunnelSession : INotifyPropertyChanged, IDisposable
{
    public const string CouldNotSave = "Could not save the session.";

    private readonly TunnelStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Func<ITunnelRuntime>? _createRuntime;
    private readonly Dictionary<Guid, HopSlot> _slots = [];
    private bool _disposed;
    private string _statusText = "0 of 0 running";
    private string? _saveError;
    private string? _notice;

    public TunnelSession(
        TunnelStore? store = null,
        Dispatcher? dispatcher = null,
        Func<ITunnelRuntime>? createRuntime = null)
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

    public bool HasTunnels => Tunnels.Count > 0;

    public bool ShowNoTunnelsHint => Tunnels.Count == 0;

    public int RunningCount => Tunnels.Count(item => item.Status == TunnelStatus.Running);

    public TunnelLoadResult Load()
    {
        var loaded = _store.Load();
        if (loaded.Status is TunnelLoadStatus.Ok or TunnelLoadStatus.Missing)
            ReplaceItems(loaded.Tunnels);
        return loaded;
    }

    public string? WriteExport(string path, IReadOnlyList<TunnelRecord> records)
    {
        if (_disposed)
            return TunnelTransfer.CouldNotWrite;
        try
        {
            TunnelTransfer.Write(path, records);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TunnelTransfer.CouldNotWrite;
        }
    }

    public async Task StartEnabledAsync()
    {
        if (_disposed)
            return;

        var tasks = Tunnels.Where(item => item.Enabled && !item.IsBusy).Select(StartItemAsync);
        await Task.WhenAll(tasks).ConfigureAwait(true);
        if (_disposed)
            return;
        RefreshSummary();
    }

    public bool TryAdd(TunnelRecord draft, out string error)
    {
        error = "";
        if (_disposed)
            return false;

        var record = draft.Clone();
        record.Id = Guid.NewGuid();
        record.Name = draft.Name.Trim();
        record.Enabled = false;
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

    public bool TryUpdate(TunnelItem item, TunnelRecord draft, out string error)
    {
        error = "";
        if (_disposed)
            return false;

        var next = item.Record.Clone();
        CopyEndpoints(draft, next);
        next.Name = draft.Name.Trim();
        if (!TryValidate(next, ignoreId: item.Id, out error))
            return false;

        var previous = item.Record.Clone();
        CopyEndpoints(next, item.Record);
        item.Record.Name = next.Name;
        item.RefreshIdentity();
        if (!Persist())
        {
            CopyEndpoints(previous, item.Record);
            item.Record.Name = previous.Name;
            item.RefreshIdentity();
            error = CouldNotSave;
            RefreshSummary();
            return false;
        }

        RefreshSummary();
        return true;
    }

    public async Task ApplyEditAsync(TunnelItem item, TunnelRecord draft)
    {
        if (_disposed || item.IsBusy)
            return;
        if (!TryUpdate(item, draft, out var error))
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
        if (!PersistRecords(Tunnels.Where(other => other != item).Select(other => other.Record)))
        {
            RefreshSummary();
            return;
        }

        _slots.Remove(item.Id);
        Tunnels.Remove(item);
        NotifyListFlags();
        RefreshSummary();
    }

    public async Task SetEnabledAsync(TunnelItem item, bool enabled)
    {
        if (_disposed || item.IsBusy)
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
        var pending = _slots.Values.Where(slot => slot.Runtime is not null).Select(StopSlotAsync).ToArray();
        if (pending.Length > 0)
            await Task.WhenAll(pending).ConfigureAwait(true);
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

        _slots.Clear();
        Tunnels.Clear();
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
            copies.Add(item.Record.Clone());
        return copies;
    }

    public bool TryMove(TunnelItem item, int index)
    {
        if (_disposed)
            return false;
        var old = Tunnels.IndexOf(item);
        if (old < 0 || index < 0 || index >= Tunnels.Count)
            return false;
        if (old == index)
            return true;

        Tunnels.Move(old, index);
        if (Persist())
        {
            RefreshSummary();
            return true;
        }

        var shifted = Tunnels.IndexOf(item);
        if (shifted >= 0 && shifted != old)
            Tunnels.Move(shifted, old);
        RefreshSummary();
        return false;
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
            record.Enabled = incoming.Enabled;
            record.Name = incoming.Name.Trim();
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
        foreach (var slot in _slots.Values)
        {
            slot.Runtime?.Dispose();
            slot.Runtime = null;
        }

        foreach (var item in Tunnels)
            item.SetRuntime(null);
    }

    private async Task StartItemAsync(TunnelItem item)
    {
        if (_disposed || !item.Enabled)
            return;
        if (_slots.TryGetValue(item.Id, out var existing) && existing.Runtime?.IsRunning == true)
            return;
        if (!item.TryBeginBusy(TunnelStatus.Starting))
            return;

        if (existing?.Runtime is { } previous)
        {
            previous.Dispose();
            if (existing.Runtime == previous)
                existing.Runtime = null;
        }

        if (_disposed)
        {
            item.SetBusy(false);
            return;
        }

        var slot = existing ?? new HopSlot(item.Id);
        _slots[item.Id] = slot;
        var runtime = _createRuntime?.Invoke() ?? new SocksHopRuntime();
        var id = item.Id;
        void OnExit(int code) => OnRuntimeExited(id, runtime, code);
        runtime.Exited += OnExit;
        slot.Runtime = runtime;
        slot.Stopping = false;
        slot.Status = TunnelStatus.Starting;
        item.SetRuntime(runtime);
        try
        {
            await runtime.StartAsync(item.Record, line => PostLog(id, line), CancellationToken.None).ConfigureAwait(true);
            if (_disposed || slot.Runtime != runtime)
            {
                runtime.Exited -= OnExit;
                runtime.Dispose();
                if (slot.Runtime == runtime)
                    slot.Runtime = null;
                if (item.Runtime == runtime)
                    item.SetRuntime(null);
                item.SetBusy(false);
                return;
            }

            if (runtime.IsRunning)
            {
                slot.Status = TunnelStatus.Running;
                item.SetBusy(false, TunnelStatus.Running);
            }
            else
                Fail(item, slot, runtime, "The tunnel is not running.");
        }
        catch (Exception ex)
        {
            if (_disposed)
            {
                runtime.Exited -= OnExit;
                runtime.Dispose();
                if (slot.Runtime == runtime)
                    slot.Runtime = null;
                if (item.Runtime == runtime)
                    item.SetRuntime(null);
                item.SetBusy(false);
                return;
            }

            Fail(item, slot, runtime, ex.Message);
        }

        if (!_disposed)
            RefreshSummary();
    }

    private async Task StopItemAsync(TunnelItem item)
    {
        if (_slots.TryGetValue(item.Id, out var slot))
            await StopSlotAsync(slot).ConfigureAwait(true);
        else
            MarkIdle(item, unexpected: false);
    }

    private async Task StopSlotAsync(HopSlot slot)
    {
        if (_disposed)
            return;
        var item = FindItem(slot.Id);
        var runtime = slot.Runtime;
        if (runtime is null)
        {
            if (item is not null)
                MarkIdle(item, unexpected: false);
            _slots.Remove(slot.Id);
            return;
        }

        if (item is not null && !item.TryBeginBusy(TunnelStatus.Stopping) && item.Status is not TunnelStatus.Stopping)
            return;

        slot.Stopping = true;
        slot.Status = TunnelStatus.Stopping;
        try
        {
            await runtime.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!_disposed && item is not null)
                item.AppendLog(ex.Message);
        }
        finally
        {
            if (slot.Runtime == runtime)
            {
                runtime.Dispose();
                slot.Runtime = null;
            }

            if (item?.Runtime == runtime)
                item.SetRuntime(null);
            _slots.Remove(slot.Id);
            if (!_disposed && item is not null)
            {
                item.SetBusy(false);
                MarkIdle(item, unexpected: false);
            }
        }
    }

    private void PostLog(Guid id, string line)
    {
        if (_disposed)
            return;
        if (_dispatcher.CheckAccess())
            FindItem(id)?.AppendLog(line);
        else
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                    FindItem(id)?.AppendLog(line);
            });
        }
    }

    private void OnRuntimeExited(Guid id, ITunnelRuntime runtime, int code)
    {
        void Apply()
        {
            if (_disposed || !_slots.TryGetValue(id, out var slot) || slot.Runtime != runtime || slot.Stopping)
                return;
            var item = FindItem(id);
            item?.AppendLog($"Process ended ({code}).");
            runtime.Dispose();
            if (slot.Runtime == runtime)
                slot.Runtime = null;
            slot.Status = TunnelStatus.Failed;
            if (item is not null && item.Runtime == runtime)
            {
                item.SetRuntime(null);
                item.Mark(TunnelStatus.Failed);
            }

            _notice = null;
            RefreshSummary();
        }

        if (_dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.BeginInvoke(Apply);
    }

    private void Fail(TunnelItem item, HopSlot slot, ITunnelRuntime runtime, string message)
    {
        if (_disposed)
            return;
        item.AppendLog(message);
        runtime.Dispose();
        if (slot.Runtime == runtime)
            slot.Runtime = null;
        slot.Status = TunnelStatus.Failed;
        if (item.Runtime == runtime)
            item.SetRuntime(null);
        item.Mark(TunnelStatus.Failed);
        _notice = null;
    }

    private static void MarkIdle(TunnelItem item, bool unexpected) =>
        item.Mark(unexpected ? TunnelStatus.Failed : TunnelStatus.Stopped);

    private bool TryValidate(TunnelRecord record, Guid? ignoreId, out string error)
    {
        if (string.IsNullOrWhiteSpace(record.Name))
        {
            error = "Name is required.";
            return false;
        }

        if (Tunnels.Any(item => item.Id != ignoreId && item.Name.Equals(record.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            error = "A tunnel with this name already exists.";
            return false;
        }

        if (!record.TryValidateEndpoints(out error))
            return false;

        var occupied = Tunnels
            .Where(item => item.Id != ignoreId)
            .Select(item => (item.Record.ListenAddress, item.Record.ListenPort))
            .ToList();
        if (ListenOccupancy.ConflictsWithAny(record.ListenAddress, record.ListenPort, occupied))
        {
            error = "Another tunnel already uses this listen address and port.";
            return false;
        }

        error = "";
        return true;
    }

    private bool Persist() => PersistRecords(Tunnels.Select(item => item.Record));

    private bool PersistRecords(IEnumerable<TunnelRecord> records)
    {
        try
        {
            _store.Save(records);
            _saveError = null;
            _notice = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _saveError = CouldNotSave;
            return false;
        }
    }

    private void ReplaceItems(IReadOnlyList<TunnelRecord> records)
    {
        Tunnels.Clear();
        foreach (var record in records)
        {
            var item = CreateItem(record.Clone());
            if (_slots.TryGetValue(item.Id, out var slot))
            {
                item.SetRuntime(slot.Runtime);
                if (slot.Runtime is { IsRunning: true })
                    item.Mark(TunnelStatus.Running);
                else if (slot.Status == TunnelStatus.Failed)
                    item.Mark(TunnelStatus.Failed);
            }

            Tunnels.Add(item);
        }

        NotifyListFlags();
        RefreshSummary();
    }

    private static TunnelItem CreateItem(TunnelRecord record) => new(record);

    private TunnelItem? FindItem(Guid id)
    {
        foreach (var item in Tunnels)
        {
            if (item.Id == id)
                return item;
        }

        return null;
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(RunningCount));
        if (!string.IsNullOrWhiteSpace(_saveError))
        {
            StatusText = _saveError;
            return;
        }

        var live = Tunnels.Any(item => item.Status is TunnelStatus.Running or TunnelStatus.Failed);
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

        StatusText = $"{RunningCount} of {Tunnels.Count} running";
    }

    private void NotifyListFlags()
    {
        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(ShowNoTunnelsHint));
    }

    private static void CopyEndpoints(TunnelRecord from, TunnelRecord to)
    {
        to.ListenAddress = from.ListenAddress;
        to.ListenPort = from.ListenPort;
        to.SocksHost = from.SocksHost;
        to.SocksPort = from.SocksPort;
        to.RemoteHost = from.RemoteHost;
        to.RemotePort = from.RemotePort;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class HopSlot(Guid id)
    {
        public Guid Id { get; } = id;
        public ITunnelRuntime? Runtime { get; set; }
        public TunnelStatus Status { get; set; }
        public bool Stopping { get; set; }
    }
}
