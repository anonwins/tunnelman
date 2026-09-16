using System.Text.Json.Nodes;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelStoreTests
{
    [Fact]
    public void Round_trip_preserves_enabled_and_unknown_kind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-store-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var store = new TunnelStore(file);
            var unknown = new TunnelRecord
            {
                Id = Guid.NewGuid(),
                Name = "future",
                Kind = "rathole",
                Enabled = true,
                Options = new JsonObject { ["token"] = "abc" },
            };
            var ncat = new TunnelRecord
            {
                Id = Guid.NewGuid(),
                Name = "ssh",
                Kind = TunnelKinds.NcatSocks,
                Enabled = false,
                Options = new NcatSocksOptions { RemoteHost = "example.test" }.ToJson(),
            };
            store.Save([unknown, ncat]);

            var loaded = store.Load();
            Assert.Null(loaded.Error);
            Assert.False(loaded.Corrupt);
            Assert.Equal(2, loaded.Tunnels.Count);
            Assert.Equal("rathole", loaded.Tunnels[0].Kind);
            Assert.True(loaded.Tunnels[0].Enabled);
            Assert.Equal("abc", loaded.Tunnels[0].Options?["token"]?.GetValue<string>());
            Assert.Equal(TunnelKinds.NcatSocks, loaded.Tunnels[1].Kind);
            Assert.False(loaded.Tunnels[1].Enabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_file_is_copied_to_bak()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-store-bad-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        File.WriteAllText(file, "{not json");
        try
        {
            var store = new TunnelStore(file);
            var loaded = store.Load();
            Assert.True(loaded.Corrupt);
            Assert.Empty(loaded.Tunnels);
            Assert.True(File.Exists(file + ".bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unsupported_version_is_corrupt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-store-ver-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        File.WriteAllText(file, """{"version": 2, "tunnels": []}""");
        try
        {
            var loaded = new TunnelStore(file).Load();
            Assert.True(loaded.Corrupt);
            Assert.Empty(loaded.Tunnels);
            Assert.Contains("version", loaded.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(file + ".bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Clear_writes_empty_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-store-clear-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var store = new TunnelStore(file);
            store.Save([
                new TunnelRecord { Id = Guid.NewGuid(), Name = "a", Kind = TunnelKinds.NcatSocks, Options = new JsonObject() },
            ]);
            store.Clear();
            var loaded = store.Load();
            Assert.Empty(loaded.Tunnels);
            Assert.False(loaded.Corrupt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
