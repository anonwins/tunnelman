using System.Text.Json.Nodes;
using System.Windows.Threading;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelSessionTests
{
    [Fact]
    public void Load_restores_enabled_flags_and_does_not_start_processes()
    {
        var dir = NewDir("tunnelman-session-");
        try
        {
            var store = new TunnelStore(Path.Combine(dir, "tunnels.json"));
            store.Save([
                new TunnelRecord
                {
                    Id = Guid.NewGuid(),
                    Name = "up",
                    Kind = TunnelKinds.NcatSocks,
                    Enabled = true,
                    Options = Hop(22001),
                },
                new TunnelRecord
                {
                    Id = Guid.NewGuid(),
                    Name = "down",
                    Kind = TunnelKinds.NcatSocks,
                    Enabled = false,
                    Options = Hop(22002),
                },
            ]);

            var session = new TunnelSession(store, Dispatcher.CurrentDispatcher);
            session.Load();
            Assert.Equal(2, session.Tunnels.Count);
            Assert.True(session.Tunnels[0].Enabled);
            Assert.False(session.Tunnels[1].Enabled);
            Assert.Equal(0, session.RunningCount);
            Assert.All(session.Tunnels, t => Assert.Null(t.Runtime));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Load_marks_unknown_kind_unsupported()
    {
        var dir = NewDir("tunnelman-session-kind-");
        try
        {
            var store = new TunnelStore(Path.Combine(dir, "tunnels.json"));
            store.Save([
                new TunnelRecord
                {
                    Id = Guid.NewGuid(),
                    Name = "future",
                    Kind = "rathole",
                    Enabled = true,
                    Options = new JsonObject { ["token"] = "abc" },
                },
            ]);

            var session = new TunnelSession(store, Dispatcher.CurrentDispatcher);
            session.Load();
            var item = Assert.Single(session.Tunnels);
            Assert.Equal(TunnelStatus.Unsupported, item.Status);
            Assert.True(item.CanToggle);
            Assert.False(item.IsSupported);

            Assert.True(item.Enabled);
            await session.SetEnabledAsync(item, false);
            Assert.False(item.Enabled);
            Assert.False(item.CanToggle);
            Assert.Equal(TunnelStatus.Unsupported, item.Status);
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
            Assert.False(session.TryAdd("   ", TunnelKinds.NcatSocks, Hop(22001), out var error));
            Assert.Contains("Name", error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(session.Tunnels);
        });
    }

    [Fact]
    public void TryAdd_rejects_duplicate_name()
    {
        WithEmpty(session =>
        {
            Assert.True(session.TryAdd("One", TunnelKinds.NcatSocks, Hop(22001), out _));
            Assert.False(session.TryAdd("one", TunnelKinds.NcatSocks, Hop(22002), out var error));
            Assert.Contains("already exists", error, StringComparison.OrdinalIgnoreCase);
            Assert.Single(session.Tunnels);
        });
    }

    [Fact]
    public void TryAdd_rejects_unknown_kind()
    {
        WithEmpty(session =>
        {
            Assert.False(session.TryAdd("x", "rathole", new JsonObject(), out var error));
            Assert.Contains("Unknown", error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(session.Tunnels);
        });
    }

    [Fact]
    public void TryAdd_rejects_duplicate_listen_and_persists()
    {
        var dir = NewDir("tunnelman-session-add-");
        try
        {
            var file = Path.Combine(dir, "tunnels.json");
            var session = new TunnelSession(new TunnelStore(file), Dispatcher.CurrentDispatcher);
            session.Load();
            var options = Hop(22001);
            Assert.True(session.TryAdd("one", TunnelKinds.NcatSocks, options, out _));
            Assert.False(session.TryAdd("two", TunnelKinds.NcatSocks, (JsonObject)options.DeepClone(), out var error));
            Assert.Contains("listen", error, StringComparison.OrdinalIgnoreCase);

            var reloaded = new TunnelStore(file).Load();
            Assert.Single(reloaded.Tunnels);
            Assert.Equal("one", reloaded.Tunnels[0].Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryAdd_reports_save_error_when_store_path_is_a_directory()
    {
        var dir = NewDir("tunnelman-session-save-");
        try
        {
            var blocked = Path.Combine(dir, "tunnels.json");
            Directory.CreateDirectory(blocked);
            var session = new TunnelSession(new TunnelStore(blocked), Dispatcher.CurrentDispatcher);
            session.Load();
            Assert.False(session.TryAdd("one", TunnelKinds.NcatSocks, Hop(22001), out var error));
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
            Assert.True(session.TryAdd("one", TunnelKinds.NcatSocks, Hop(22001), out _));
            session.Tunnels[0].Enabled = true;
            var snap = session.SnapshotRecords();
            Assert.False(Assert.Single(snap).Enabled);
            Assert.True(session.Tunnels[0].Enabled);
            snap[0].Options!["remoteHost"] = "mutated.test";
            Assert.Equal("example.test", session.Tunnels[0].Record.Options?["remoteHost"]?.GetValue<string>());
        });
    }

    [Fact]
    public void TryImport_mints_ids_stays_off_and_rolls_back()
    {
        var dir = NewDir("tunnelman-session-import-");
        try
        {
            var session = new TunnelSession(new TunnelStore(Path.Combine(dir, "tunnels.json")), Dispatcher.CurrentDispatcher);
            session.Load();
            var original = Guid.NewGuid();
            var incoming = new TunnelRecord
            {
                Id = original,
                Name = "from-file",
                Kind = TunnelKinds.NcatSocks,
                Enabled = true,
                Options = Hop(22001),
            };
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
            Assert.True(session.TryAdd("two", TunnelKinds.NcatSocks, Hop(22002), out _));
            Assert.DoesNotContain("Exported", session.StatusText, StringComparison.Ordinal);

            var blocked = Path.Combine(dir, "blocked.json");
            Directory.CreateDirectory(blocked);
            var fail = new TunnelSession(new TunnelStore(blocked), Dispatcher.CurrentDispatcher);
            fail.Load();
            Assert.False(fail.TryImport([
                new TunnelRecord { Name = "x", Kind = TunnelKinds.NcatSocks, Options = Hop(22001) },
            ], out var none, out var error));
            Assert.Equal(0, none);
            Assert.Contains("Could not save", error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(fail.Tunnels);
            Assert.Contains("Could not save", fail.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Persist_blocked_load_refuses_add_and_keeps_bytes()
    {
        var dir = NewDir("tunnelman-session-block-");
        var file = Path.Combine(dir, "tunnels.json");
        const string original = """{"version":1,"tunnels":[]}""";
        File.WriteAllText(file, original);
        try
        {
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var session = new TunnelSession(new TunnelStore(file), Dispatcher.CurrentDispatcher);
                session.Load();
                Assert.False(session.TryAdd("one", TunnelKinds.NcatSocks, Hop(22001), out var error));
                Assert.Contains("Could not save", error, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(session.Tunnels);
            }

            Assert.Equal(original, File.ReadAllText(file));
            Assert.False(File.Exists(file + ".bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Enable_delete_edit_and_clear_roll_back_when_save_fails()
    {
        var dir = NewDir("tunnelman-session-rollback-");
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var session = new TunnelSession(new TunnelStore(file), Dispatcher.CurrentDispatcher);
            session.Load();
            Assert.True(session.TryAdd("one", TunnelKinds.NcatSocks, Hop(22001), out _));
            var item = Assert.Single(session.Tunnels);
            BlockWrites(file);

            await session.SetEnabledAsync(item, true);
            Assert.False(item.Enabled);
            Assert.Null(item.Runtime);

            await session.DeleteAsync(item);
            Assert.Single(session.Tunnels);

            Assert.False(session.TryUpdate(item, "renamed", TunnelKinds.NcatSocks, Hop(22001), out _));
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
    public async Task StartEnabled_missing_ncat_marks_failed_not_unsupported()
    {
        var dir = NewDir("tunnelman-session-ncat-");
        try
        {
            var store = new TunnelStore(Path.Combine(dir, "tunnels.json"));
            store.Save([
                new TunnelRecord
                {
                    Id = Guid.NewGuid(),
                    Name = "up",
                    Kind = TunnelKinds.NcatSocks,
                    Enabled = true,
                    Options = Hop(22001),
                },
                new TunnelRecord
                {
                    Id = Guid.NewGuid(),
                    Name = "future",
                    Kind = "rathole",
                    Enabled = true,
                    Options = new JsonObject { ["token"] = "abc" },
                },
                new TunnelRecord
                {
                    Id = Guid.NewGuid(),
                    Name = "down",
                    Kind = TunnelKinds.NcatSocks,
                    Enabled = false,
                    Options = Hop(22002),
                },
            ]);

            var session = new TunnelSession(store, Dispatcher.CurrentDispatcher);
            session.Load();
            await session.StartEnabledAsync();

            Assert.Equal(TunnelStatus.Failed, session.Tunnels[0].Status);
            Assert.Contains("ncat", session.Tunnels[0].LogText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TunnelStatus.Unsupported, session.Tunnels[1].Status);
            Assert.Equal(TunnelStatus.Stopped, session.Tunnels[2].Status);
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
            Assert.True(session.TryAdd("one", TunnelKinds.NcatSocks, Hop(22001), out _));
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
            var local = new NcatSocksOptions { RemoteHost = "example.test", ListenAddress = "127.0.0.1", ListenPort = 22001 }.ToJson();
            var any = new NcatSocksOptions { RemoteHost = "example.test", ListenAddress = "0.0.0.0", ListenPort = 22001 }.ToJson();
            Assert.True(session.TryAdd("one", TunnelKinds.NcatSocks, local, out _));
            Assert.False(session.TryAdd("two", TunnelKinds.NcatSocks, any, out var error));
            Assert.Contains("listen", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Fake_runtime_identity_busy_and_dispose_barrier()
    {
        var dir = NewDir("tunnelman-session-fake-");
        try
        {
            var first = new FakeRuntime();
            var second = new FakeRuntime();
            var heldRuntime = new FakeRuntime();
            var queue = new Queue<ITunnelRuntime>([first, second, heldRuntime]);
            var session = new TunnelSession(
                new TunnelStore(Path.Combine(dir, "tunnels.json")),
                Dispatcher.CurrentDispatcher,
                _ => queue.Dequeue());
            session.Load();
            session.SetNcatForTests("ncat.exe", missing: false);
            Assert.True(session.TryAdd("one", TunnelKinds.NcatSocks, Hop(22001), out _));
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
    public void TryImport_unknown_kind_and_notice_survives_resolve()
    {
        WithEmpty(session =>
        {
            Assert.True(session.TryImport([
                new TunnelRecord { Name = "future", Kind = "rathole", Options = new JsonObject { ["token"] = "abc" } },
            ], out _, out _));
            var item = Assert.Single(session.Tunnels);
            Assert.Equal(TunnelStatus.Unsupported, item.Status);
            Assert.False(item.Enabled);
            session.SetNotice("Imported 1 tunnel.");
            session.ResolveNcat();
            Assert.Contains("Imported 1 tunnel.", session.StatusText, StringComparison.Ordinal);
        });
    }

    private static void WithEmpty(Action<TunnelSession> body)
    {
        var dir = NewDir("tunnelman-session-empty-");
        try
        {
            var session = new TunnelSession(new TunnelStore(Path.Combine(dir, "tunnels.json")), Dispatcher.CurrentDispatcher);
            session.Load();
            body(session);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void BlockWrites(string file)
    {
        if (File.Exists(file))
            File.Delete(file);
        Directory.CreateDirectory(file);
    }

    private static JsonObject Hop(int listenPort) =>
        new NcatSocksOptions { RemoteHost = "example.test", ListenPort = listenPort }.ToJson();

    private sealed class FakeRuntime : ITunnelRuntime
    {
        public bool Disposed { get; private set; }
        public bool IsRunning { get; set; }
        public TaskCompletionSource<bool>? HoldStart { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<int>? Exited;

        public async Task StartAsync(TunnelRecord record, string ncatPath, Action<string> log, CancellationToken cancellationToken)
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
