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
    public void Locked_file_is_unreadable_and_untouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-store-lock-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        const string original = """{"version":1,"tunnels":[]}""";
        File.WriteAllText(file, original);
        try
        {
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var loaded = new TunnelStore(file).Load();
                Assert.True(loaded.Unreadable);
                Assert.False(loaded.Corrupt);
                Assert.Equal(TunnelStore.CouldNotRead, loaded.Error);
                Assert.False(File.Exists(file + ".bak"));
            }

            Assert.Equal(original, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"version":1,"tunnels":null}""")]
    [InlineData("""{"version":1,"tunnels":[null]}""")]
    public void Null_tunnels_are_corrupt_with_bak(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-store-null-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        File.WriteAllText(file, json);
        try
        {
            var loaded = new TunnelStore(file).Load();
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
    public void Omitted_tunnels_and_version_are_clean()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-store-omit-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            File.WriteAllText(file, "{}");
            var empty = new TunnelStore(file).Load();
            Assert.False(empty.Corrupt);
            Assert.False(empty.Unreadable);
            Assert.Null(empty.Error);
            Assert.Empty(empty.Tunnels);

            File.WriteAllText(file, """{"tunnels":[]}""");
            var version = new TunnelStore(file).Load();
            Assert.False(version.Corrupt);
            Assert.Empty(version.Tunnels);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Export_format_on_session_path_rewrites_empty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-store-export-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        File.WriteAllText(file, """{"format":"tunnelman-export","version":1,"tunnels":[{"name":"x","kind":"ncat-socks"}]}""");
        try
        {
            var store = new TunnelStore(file);
            var first = store.Load();
            Assert.True(first.Corrupt);
            Assert.True(first.BakCopied);
            Assert.Empty(first.Tunnels);
            Assert.True(File.Exists(file + ".bak"));
            Assert.Contains("\"version\": 1", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.DoesNotContain("tunnelman-export", File.ReadAllText(file), StringComparison.Ordinal);

            var second = store.Load();
            Assert.False(second.Corrupt);
            Assert.False(second.Unreadable);
            Assert.Null(second.Error);
            Assert.Empty(second.Tunnels);
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
