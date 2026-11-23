using System.Windows.Threading;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelSessionTests
{
    [Fact]
    public void Unlock_restores_enabled_flags_and_does_not_start()
    {
        var dir = SessionTestHost.NewDir("tunnelman-session-");
        try
        {
            var up = SessionTestHost.Hop("up", 22001);
            up.Id = Guid.NewGuid();
            up.Enabled = true;
            var down = SessionTestHost.Hop("down", 22002);
            down.Id = Guid.NewGuid();
            var session = SessionTestHost.Open(dir, Dispatcher.CurrentDispatcher, records: [up, down]);
            Assert.Equal(2, session.Tunnels.Count);
            Assert.True(session.Tunnels[0].Enabled);
            Assert.False(session.Tunnels[1].Enabled);
            Assert.Equal(0, session.RunningCount);
            Assert.All(session.Tunnels, item => Assert.Null(item.Runtime));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryAdd_rejects_empty_name()
    {
        WithEmpty(session =>
        {
            Assert.False(session.TryAdd(SessionTestHost.Hop("   ", 22001), out var error));
            Assert.Equal("Name is required.", error);
        });
    }

    [Fact]
    public void TryAdd_rejects_duplicate_name()
    {
        WithEmpty(session =>
        {
            Assert.True(session.TryAdd(SessionTestHost.Hop("One", 22001), out _));
            Assert.False(session.TryAdd(SessionTestHost.Hop("one", 22002), out var error));
            Assert.Contains("name", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void TryAdd_rejects_duplicate_listen_and_persists()
    {
        var dir = SessionTestHost.NewDir("tunnelman-session-listen-");
        try
        {
            var session = SessionTestHost.Open(dir, Dispatcher.CurrentDispatcher);
            var hop = SessionTestHost.Hop("one", 22001);
            Assert.True(session.TryAdd(hop, out _));
            var again = hop.Clone();
            again.Name = "two";
            Assert.False(session.TryAdd(again, out var error));
            Assert.Contains("listen", error, StringComparison.OrdinalIgnoreCase);

            var reopened = new TunnelSession(new TunnelStore(Path.Combine(dir, "tunnels.json")), Dispatcher.CurrentDispatcher);
            Assert.True(reopened.TryUnlock(SessionTestHost.Passphrase, out _));
            Assert.Equal("one", Assert.Single(reopened.Tunnels).Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryAdd_reports_save_error_when_store_path_is_a_directory()
    {
        var dir = SessionTestHost.NewDir("tunnelman-session-save-");
        try
        {
            var session = SessionTestHost.Open(dir, Dispatcher.CurrentDispatcher);
            BlockWrites(Path.Combine(dir, "tunnels.json"));
            Assert.False(session.TryAdd(SessionTestHost.Hop("one", 22001), out var error));
            Assert.Contains("Could not save", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Could not save", session.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(session.Tunnels);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SnapshotRecords_disables_copies_only()
    {
        WithEmpty(session =>
        {
            Assert.True(session.TryAdd(SessionTestHost.Hop("one", 22001), out _));
            session.Tunnels[0].Enabled = true;
            var snap = session.SnapshotRecords();
            Assert.False(Assert.Single(snap).Enabled);
            Assert.True(session.Tunnels[0].Enabled);
            snap[0].RemoteHost = "mutated.test";
            Assert.Equal("example.test", session.Tunnels[0].Record.RemoteHost);
        });
    }

    [Fact]
    public void TryImport_mints_ids_stays_off_and_rolls_back()
    {
        var dir = SessionTestHost.NewDir("tunnelman-session-import-");
        try
        {
            var session = SessionTestHost.Open(dir, Dispatcher.CurrentDispatcher);
            var original = Guid.NewGuid();
            var incoming = SessionTestHost.Hop("from-file", 22001);
            incoming.Id = original;
            incoming.Enabled = true;
            Assert.True(session.TryImport([incoming], out var added, out _));
            Assert.Equal(1, added);
            var item = Assert.Single(session.Tunnels);
            Assert.NotEqual(original, item.Id);
            Assert.False(item.Enabled);
            Assert.Null(item.Runtime);
            Assert.Equal(0, session.RunningCount);
            Assert.Contains("Imported 1 tunnel.", session.StatusText, StringComparison.Ordinal);

            session.SetNotice("Exported 1 tunnel.");
            Assert.Contains("Exported", session.StatusText, StringComparison.Ordinal);
            Assert.True(session.TryAdd(SessionTestHost.Hop("two", 22002), out _));
            Assert.DoesNotContain("Exported", session.StatusText, StringComparison.Ordinal);

            BlockWrites(Path.Combine(dir, "tunnels.json"));
            Assert.False(session.TryImport([SessionTestHost.Hop("x", 22003)], out var none, out var error));
            Assert.Equal(0, none);
            Assert.Contains("Could not save", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, session.Tunnels.Count);
            Assert.Contains("Could not save", session.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Enable_delete_edit_and_clear_roll_back_when_save_fails()
    {
        var dir = SessionTestHost.NewDir("tunnelman-session-rollback-");
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var session = SessionTestHost.Open(dir, Dispatcher.CurrentDispatcher);
            Assert.True(session.TryAdd(SessionTestHost.Hop("one", 22001), out _));
            var item = Assert.Single(session.Tunnels);
            BlockWrites(file);

            await session.SetEnabledAsync(item, true);
            Assert.False(item.Enabled);
            Assert.Null(item.Runtime);

            await session.DeleteAsync(item);
            Assert.Single(session.Tunnels);

            var renamed = item.Record.Clone();
            renamed.Name = "renamed";
            Assert.False(session.TryUpdate(item, renamed, out _));
            Assert.Equal("one", item.Name);

            Assert.False(await session.ClearSessionAsync());
            Assert.Single(session.Tunnels);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Notice_yields_to_running_count()
    {
        WithEmpty(session =>
        {
            Assert.True(session.TryAdd(SessionTestHost.Hop("one", 22001), out _));
            session.Tunnels[0].SetBusy(false, TunnelStatus.Running);
            session.SetNotice("Exported 1 tunnel.");
            Assert.Contains("running", session.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exported", session.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TryAdd_rejects_unspecified_listen_overlap()
    {
        WithEmpty(session =>
        {
            Assert.True(session.TryAdd(SessionTestHost.Hop("one", 22001), out _));
            Assert.False(session.TryAdd(SessionTestHost.Hop("two", 22001, "0.0.0.0"), out var error));
            Assert.Contains("listen", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Fake_runtime_identity_busy_and_dispose_barrier()
    {
        var dir = SessionTestHost.NewDir("tunnelman-session-fake-");
        try
        {
            var first = new FakeRuntime();
            var second = new FakeRuntime();
            var heldRuntime = new FakeRuntime();
            var queue = new Queue<ITunnelRuntime>([first, second, heldRuntime]);
            var session = SessionTestHost.Open(dir, Dispatcher.CurrentDispatcher, () => queue.Dequeue());
            Assert.True(session.TryAdd(SessionTestHost.Hop("one", 22001), out _));
            var item = session.Tunnels[0];
            item.Enabled = true;

            await session.StartEnabledAsync();
            Assert.Same(first, item.Runtime);
            Assert.Equal(TunnelStatus.Running, item.Status);

            first.IsRunning = false;
            await session.StartEnabledAsync();
            Assert.Same(second, item.Runtime);
            Assert.True(first.Disposed);
            first.RaiseExited(1);
            Assert.False(second.Disposed);
            Assert.Same(second, item.Runtime);

            heldRuntime.HoldStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            second.IsRunning = false;
            var held = session.StartEnabledAsync();
            await heldRuntime.Entered.Task;
            Assert.True(item.IsBusy);
            await session.DeleteAsync(item);
            Assert.Single(session.Tunnels);
            await session.StartEnabledAsync();
            Assert.True(item.IsBusy);

            session.Dispose();
            heldRuntime.HoldStart.TrySetResult(true);
            await held;
            await session.StartEnabledAsync();
            await session.DeleteAsync(item);
            Assert.Single(session.Tunnels);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Lock_hides_rows_and_keeps_the_runtime()
    {
        var dir = SessionTestHost.NewDir("tunnelman-session-lock-");
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var runtime = new FakeRuntime();
            var session = SessionTestHost.Open(dir, Dispatcher.CurrentDispatcher, () => runtime);
            Assert.True(session.TryAdd(SessionTestHost.Hop("one", 22001), out _));
            session.Tunnels[0].Enabled = true;
            await session.StartEnabledAsync();
            var before = File.ReadAllBytes(file);

            Assert.True(session.TryLock());
            Assert.Empty(session.Tunnels);
            Assert.Empty(session.SnapshotRecords());
            Assert.False(session.TryAdd(SessionTestHost.Hop("two", 22002), out _));
            Assert.Equal(1, session.RunningCount);
            Assert.Equal(before, File.ReadAllBytes(file));

            Assert.True(session.TryUnlock(SessionTestHost.Passphrase, out _));
            Assert.Same(runtime, Assert.Single(session.Tunnels).Runtime);
            Assert.False(session.ConsumeFreshUnlock());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Lock_while_start_is_held_leaves_the_row()
    {
        var dir = SessionTestHost.NewDir("tunnelman-session-hold-");
        try
        {
            var held = new FakeRuntime();
            var session = SessionTestHost.Open(dir, Dispatcher.CurrentDispatcher, () => held);
            Assert.True(session.TryAdd(SessionTestHost.Hop("one", 22001), out _));
            session.Tunnels[0].Enabled = true;
            held.HoldStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var starting = session.StartEnabledAsync();
            await held.Entered.Task;
            Assert.False(session.TryLock());
            Assert.Single(session.Tunnels);
            held.HoldStart.TrySetResult(true);
            await starting;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void WithEmpty(Action<TunnelSession> body)
    {
        var dir = SessionTestHost.NewDir("tunnelman-session-empty-");
        try
        {
            var session = SessionTestHost.Open(dir, Dispatcher.CurrentDispatcher);
            body(session);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void BlockWrites(string file)
    {
        if (File.Exists(file))
            File.Delete(file);
        Directory.CreateDirectory(file);
    }

    private sealed class FakeRuntime : ITunnelRuntime
    {
        public bool Disposed { get; private set; }
        public bool IsRunning { get; set; }
        public TaskCompletionSource<bool>? HoldStart { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<int>? Exited;

        public async Task StartAsync(TunnelRecord record, IDialSecrets secrets, Action<string> log, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            if (HoldStart is { } hold)
                await hold.Task.ConfigureAwait(false);
            IsRunning = true;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Disposed = true;
            IsRunning = false;
        }

        public void RaiseExited(int code) => Exited?.Invoke(code);
    }
}
