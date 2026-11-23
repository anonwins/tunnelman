using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelStoreTests
{
    [Fact]
    public void Round_trip_keeps_enabled_and_endpoints()
    {
        var dir = SessionTestHost.NewDir("tunnelman-store-");
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var store = new TunnelStore(file);
            var kept = SessionTestHost.Hop("ssh", 22001);
            kept.Id = Guid.NewGuid();
            kept.Enabled = true;
            store.Save([kept]);

            var text = File.ReadAllText(file);
            Assert.Contains("\"version\": 1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("kdf", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ciphertext", text, StringComparison.OrdinalIgnoreCase);

            var opened = store.Load();
            Assert.Equal(TunnelLoadStatus.Ok, opened.Status);
            var loaded = Assert.Single(opened.Tunnels);
            Assert.Equal(kept.Id, loaded.Id);
            Assert.Equal("ssh", loaded.Name);
            Assert.True(loaded.Enabled);
            Assert.Equal("example.test", loaded.RemoteHost);
            Assert.Equal(22001, loaded.ListenPort);
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

            var opened = store.Load();
            Assert.Equal(TunnelLoadStatus.Foreign, opened.Status);
            Assert.Equal(original, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Old_envelope_is_foreign_and_untouched()
    {
        var dir = SessionTestHost.NewDir("tunnelman-store-envelope-");
        var file = Path.Combine(dir, "tunnels.json");
        const string original = """
            {
              "version": 1,
              "kdf": "pbkdf2-sha256",
              "iterations": 600000,
              "salt": "gpFqhOJVyz1s88WPXC0OdQ==",
              "nonce": "pbQZ+HQ4Po6RMAJ5",
              "ciphertext": "AAAA"
            }
            """;
        File.WriteAllText(file, original);
        try
        {
            var store = new TunnelStore(file);
            Assert.Equal(TunnelProbeKind.Foreign, store.Probe().Kind);
            Assert.Equal(TunnelLoadStatus.Foreign, store.Load().Status);
            Assert.Equal(original, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unreadable_file_is_left_untouched()
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
            store.Save([SessionTestHost.Hop("a", 22001)]);
            store.Save([]);
            var opened = store.Load();
            Assert.Empty(opened.Tunnels);
            Assert.Contains("\"version\": 1", File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
