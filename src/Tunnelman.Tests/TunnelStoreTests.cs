using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelStoreTests
{
    [Fact]
    public void Round_trip_uses_iterations_from_the_file()
    {
        var dir = SessionTestHost.NewDir("tunnelman-store-");
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var store = new TunnelStore(file);
            var kept = SessionTestHost.Hop("ssh", 22001);
            kept.Id = Guid.NewGuid();
            kept.Enabled = true;
            var key = store.Create(SessionTestHost.Passphrase, iterations: 4, [kept]);
            Assert.NotNull(key);
            key.Dispose();

            var text = File.ReadAllText(file);
            Assert.Contains("\"iterations\": 4", text, StringComparison.Ordinal);
            Assert.DoesNotContain("600000", text, StringComparison.Ordinal);

            var opened = store.Open(SessionTestHost.Passphrase);
            Assert.Equal(TunnelOpenStatus.Ok, opened.Status);
            var loaded = Assert.Single(opened.Tunnels);
            Assert.Equal(kept.Id, loaded.Id);
            Assert.Equal("ssh", loaded.Name);
            Assert.True(loaded.Enabled);
            Assert.Equal("example.test", loaded.RemoteHost);
            Assert.Equal(22001, loaded.ListenPort);
            opened.Key?.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Wrong_passphrase_leaves_the_file_unchanged()
    {
        var dir = SessionTestHost.NewDir("tunnelman-store-wrong-");
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var store = new TunnelStore(file);
            var key = store.Create(SessionTestHost.Passphrase, 1, [SessionTestHost.Hop("ssh", 22001)]);
            key!.Dispose();
            var before = File.ReadAllBytes(file);

            var opened = store.Open("not-the-passphrase");
            Assert.Equal(TunnelOpenStatus.WrongPassphrase, opened.Status);
            Assert.Null(opened.Key);
            Assert.Equal(before, File.ReadAllBytes(file));
            Assert.False(File.Exists(file + ".bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Foreign_file_is_left_untouched()
    {
        var dir = SessionTestHost.NewDir("tunnelman-store-foreign-");
        var file = Path.Combine(dir, "tunnels.json");
        const string original = "{}";
        File.WriteAllText(file, original);
        try
        {
            var store = new TunnelStore(file);
            Assert.Equal(TunnelProbeKind.Foreign, store.Probe().Kind);
            Assert.Equal(original, File.ReadAllText(file));
            Assert.False(File.Exists(file + ".bak"));

            var opened = store.Open(SessionTestHost.Passphrase);
            Assert.Equal(TunnelOpenStatus.Foreign, opened.Status);
            Assert.Equal(original, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Locked_file_is_unreadable_and_untouched()
    {
        var dir = SessionTestHost.NewDir("tunnelman-store-lock-");
        var file = Path.Combine(dir, "tunnels.json");
        const string original = """{"version":1,"tunnels":[]}""";
        File.WriteAllText(file, original);
        try
        {
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var probe = new TunnelStore(file).Probe();
                Assert.Equal(TunnelProbeKind.Unreadable, probe.Kind);
                Assert.False(File.Exists(file + ".bak"));
            }

            Assert.Equal(original, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_can_clear_the_list()
    {
        var dir = SessionTestHost.NewDir("tunnelman-store-clear-");
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var store = new TunnelStore(file);
            var key = store.Create(SessionTestHost.Passphrase, 1, [SessionTestHost.Hop("a", 22001)]);
            Assert.NotNull(key);
            store.Save([], key);
            key.Dispose();
            var opened = store.Open(SessionTestHost.Passphrase);
            Assert.Empty(opened.Tunnels);
            opened.Key?.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
