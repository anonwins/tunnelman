using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Threading;

namespace Tunnelman.Tunnels;

internal enum SessionView
{
    Create,
    Unlock,
    Open,
    Locked,
}

internal sealed class TunnelSession : INotifyPropertyChanged, IDisposable
{
    public const string CouldNotSave = "Could not save the session.";
    public const string PassphraseTooShort = "Use at least 8 characters.";
    public const string PassphraseMismatch = "Passphrases do not match.";
    public const string WrongPassphrase = "Wrong passphrase.";

    private readonly TunnelStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Func<ITunnelRuntime>? _createRuntime;
    private readonly Dictionary<Guid, HopSlot> _slots = [];
    private SessionKey? _key;
    private SessionView _mode = SessionView.Unlock;
    private bool _disposed;
    private bool _exitPrompt;
    private bool _freshUnlock;
    private string _statusText = "Enter the passphrase to continue.";
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

    public SessionView Mode => _mode;

    public bool ShowSession => _mode == SessionView.Open;

    public bool ShowGate => _mode != SessionView.Open;

    public bool ShowConfirm => _mode == SessionView.Create;

    public bool IsLocked => _mode == SessionView.Locked;

    public bool CanLock => ShowSession && !AnyBusy && !_exitPrompt;

    public string LockToolTip => CanLock
        ? "Hide the tunnel list until the passphrase is entered."
        : "Wait until tunnels finish starting or stopping.";

    public string GateTitle => _mode switch
    {
        SessionView.Create => "Protect this session",
        SessionView.Locked => "Session locked",
        _ => "Unlock Tunnelman",
    };

    public string GateBody => _mode switch
    {
        SessionView.Create => "This passphrase encrypts the saved tunnels. Tunnelman asks for it at startup and after a lock.",
        SessionView.Locked => Tunnelman.UiSettings.Current.StopDialingWhenLocked
            ? "Connections that are already open stay up. New connections wait until you unlock."
            : "Tunnels keep running. Their hosts stay hidden until you unlock.",
        _ => "Enter the passphrase to show the saved tunnels. Enabled tunnels start after they appear.",
    };

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

    public bool HasTunnels => ShowSession && Tunnels.Count > 0;

    public bool ShowNoTunnelsHint => ShowSession && Tunnels.Count == 0;

    public int RunningCount => _mode == SessionView.Locked
        ? _slots.Values.Count(slot => slot.Runtime?.IsRunning == true)
        : Tunnels.Count(item => item.Status == TunnelStatus.Running);

    internal SessionKey? Key => _mode == SessionView.Open ? _key : null;

    internal bool HasFileKey => _key is not null;

    public bool AnyBusy => Tunnels.Any(item => item.IsBusy);

    public void PrepareCreate() => SetMode(SessionView.Create);

    public void PrepareUnlock() => SetMode(SessionView.Unlock);

    public void SetExitPrompt(bool open)
    {
        if (_exitPrompt == open)
            return;
        _exitPrompt = open;
        OnPropertyChanged(nameof(CanLock));
    }

    public static bool TryValidateNewPassphrase(string passphrase, string confirm, out string error)
    {
        if (passphrase.Length < 8)
        {
            error = PassphraseTooShort;
            return false;
        }

        if (!string.Equals(passphrase, confirm, StringComparison.Ordinal))
        {
            error = PassphraseMismatch;
            return false;
        }

        error = "";
        return true;
    }

    public SessionKey? CreateFile(string passphrase, int iterations)
    {
        try
        {
            return _store.Create(passphrase, iterations, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void ApplyCreated(SessionKey key)
    {
        _key?.Dispose();
        _key = key;
        _freshUnlock = false;
        _saveError = null;
        _notice = null;
        ReplaceItems([]);
        SetMode(SessionView.Open);
    }

    public TunnelOpenResult OpenFile(string passphrase) => _store.Open(passphrase);

    public bool TryUnlock(string passphrase, out string error)
    {
        var opened = OpenFile(passphrase);
        if (opened.Status == TunnelOpenStatus.WrongPassphrase)
        {
            error = WrongPassphrase;
            return false;
        }

        if (opened.Status != TunnelOpenStatus.Ok || opened.Key is null)
        {
            opened.Key?.Dispose();
            error = TunnelStore.CouldNotRead;
            return false;
        }

        ApplyOpened(opened);
        error = "";
        return true;
    }

    public void ApplyOpened(TunnelOpenResult opened)
    {
        if (opened.Key is null)
            return;
        var fresh = _mode is SessionView.Unlock or SessionView.Create;
        if (_key is null)
            _key = opened.Key;
        else
            opened.Key.Dispose();
        _freshUnlock = fresh;
        _saveError = null;
        ReplaceItems(opened.Tunnels);
        SetMode(SessionView.Open);
    }

    public string? WriteExport(string path, IReadOnlyList<TunnelRecord> records)
    {
        if (_mode != SessionView.Open || _key is null || !_store.TryCopyMaterial(out var salt, out var iterations))
            return TunnelTransfer.CouldNotWrite;
        try
        {
            TunnelTransfer.Write(path, records, _key, salt, iterations);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TunnelTransfer.CouldNotWrite;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public bool ConsumeFreshUnlock()
    {
        var fresh = _freshUnlock;
        _freshUnlock = false;
        return fresh;
    }

    public void SetTransientStatus(string text) => StatusText = text;

    public void RestoreStatus() => RefreshSummary();

    public bool TryLock()
    {
        if (!CanLock)
            return false;
        if (Tunnelman.UiSettings.Current.StopDialingWhenLocked)
        {
            foreach (var slot in _slots.Values)
            {
                if (slot.Runtime is { IsRunning: true } runtime)
                    runtime.SuspendDial();
            }
        }

        var items = Tunnels.ToList();
        SetMode(SessionView.Locked);
        foreach (var item in items)
        {
            item.ClearLog();
            item.SetRuntime(null);
        }

        Tunnels.Clear();
        RefreshSummary();
        _key?.Dispose();
        _key = null;
        return true;
    }

    public async Task StartEnabledAsync()
    {
        if (_disposed || _mode != SessionView.Open)
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
        if (_disposed || _mode != SessionView.Open)
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
        if (_disposed || _mode != SessionView.Open)
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
        if (_disposed || _mode != SessionView.Open || item.IsBusy)
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
        if (_disposed || _mode != SessionView.Open || item.IsBusy)
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
        if (_disposed || _mode != SessionView.Open || item.IsBusy)
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
        if (_disposed || _mode == SessionView.Locked)
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
        if (_mode != SessionView.Open)
            return [];
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
        if (_disposed || _mode != SessionView.Open)
            return false;
        if (records.Count == 0)
            return true;

        var items = new List<TunnelItem>(records.Count);
        foreach (var incoming in records)
        {
            var record = incoming.Clone();
            record.Id = Guid.NewGuid();
            record.Enabled = false;
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
        _key?.Dispose();
        _key = null;
    }

    private async Task StartItemAsync(TunnelItem item)
    {
        if (_disposed || !item.Enabled || _key is null)
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
        var dial = _key.DeriveDial();
        try
        {
            await runtime.StartAsync(item.Record, dial, line => PostLog(id, line), CancellationToken.None).ConfigureAwait(true);
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
        OnPropertyChanged(nameof(CanLock));
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
        if (_disposed || _mode == SessionView.Locked)
            return;
        if (_dispatcher.CheckAccess())
            FindItem(id)?.AppendLog(line);
        else
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && _mode != SessionView.Locked)
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
            if (item is not null && _mode != SessionView.Locked)
                item.AppendLog($"Process ended ({code}).");
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
        if (_mode != SessionView.Locked)
            item.AppendLog(message);
        runtime.Dispose();
        if (slot.Runtime == runtime)
            slot.Runtime = null;
        slot.Status = TunnelStatus.Failed;
        if (item.Runtime == runtime)
            item.SetRuntime(null);
        item.Mark(TunnelStatus.Failed);
        _notice = null;
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(LockToolTip));
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
        if (_mode == SessionView.Locked || _key is null)
        {
            _saveError = CouldNotSave;
            return false;
        }

        try
        {
            _store.Save(records, _key);
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
                if (slot.Runtime is { IsRunning: true } runtime)
                {
                    item.Mark(TunnelStatus.Running);
                    if (runtime.DialSuspended && _key is not null)
                        runtime.ResumeDial(item.Record, _key.DeriveDial());
                }
                else if (slot.Status == TunnelStatus.Failed)
                    item.Mark(TunnelStatus.Failed);
            }

            Tunnels.Add(item);
        }

        NotifyListFlags();
        RefreshSummary();
    }

    private TunnelItem CreateItem(TunnelRecord record)
    {
        var item = new TunnelItem(record);
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(TunnelItem.IsBusy) or nameof(TunnelItem.Status))
                OnPropertyChanged(nameof(CanLock));
            OnPropertyChanged(nameof(LockToolTip));
        };
        return item;
    }

    private TunnelItem? FindItem(Guid id)
    {
        foreach (var item in Tunnels)
        {
            if (item.Id == id)
                return item;
        }

        return null;
    }

    private void SetMode(SessionView mode)
    {
        _mode = mode;
        NotifyGate();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(GateBody));
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(LockToolTip));
        if (_mode == SessionView.Locked)
        {
            StatusText = "Locked";
            return;
        }

        if (_mode == SessionView.Create)
        {
            StatusText = "Set a passphrase to continue.";
            return;
        }

        if (_mode == SessionView.Unlock)
        {
            StatusText = "Enter the passphrase to continue.";
            return;
        }

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

    private void NotifyGate()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ShowSession));
        OnPropertyChanged(nameof(ShowGate));
        OnPropertyChanged(nameof(ShowConfirm));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(LockToolTip));
        OnPropertyChanged(nameof(GateTitle));
        OnPropertyChanged(nameof(GateBody));
        NotifyListFlags();
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
