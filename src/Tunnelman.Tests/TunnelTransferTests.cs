using System.Text.Json;
using System.Text.Json.Nodes;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelTransferTests
{
    [Fact]
    public void Write_then_read_export_v1_omits_enabled()
    {
        var dir = NewDir("tunnelman-xfer-write-");
        try
        {
            var file = Path.Combine(dir, "tunnels.tunnelman.json");
            var id = Guid.NewGuid();
            TunnelTransfer.Write(file, [
                new TunnelRecord
                {
                    Id = id,
                    Name = "office",
                    Kind = TunnelKinds.NcatSocks,
                    Enabled = true,
                    Options = Hop(22001),
                },
            ]);

            var text = File.ReadAllText(file);
            Assert.Contains("\"format\":", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Format\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"enabled\"", text, StringComparison.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(TunnelTransfer.FormatName, root.GetProperty("format").GetString());
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            var exportedAt = root.GetProperty("exportedAt").GetDateTimeOffset();
            Assert.Equal(TimeSpan.Zero, exportedAt.Offset);

            Assert.True(TunnelTransfer.TryRead(file, out var records, out var error));
            Assert.Equal("", error);
            var record = Assert.Single(records);
            Assert.Equal(id, record.Id);
            Assert.Equal("office", record.Name);
            Assert.Equal(TunnelKinds.NcatSocks, record.Kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_strips_secret_keys()
    {
        var dir = NewDir("tunnelman-xfer-secret-");
        try
        {
            var file = Path.Combine(dir, "out.tunnelman.json");
            TunnelTransfer.Write(file, [
                new TunnelRecord
                {
                    Name = "future",
                    Kind = "rathole",
                    Options = new JsonObject { ["token"] = "abc", ["host"] = "x" },
                },
            ]);
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("abc", text, StringComparison.Ordinal);
            Assert.Contains("\"host\"", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryRead_session_file_and_missing_version()
    {
        var dir = NewDir("tunnelman-xfer-session-");
        try
        {
            var file = Path.Combine(dir, "tunnels.json");
            File.WriteAllText(file, """{"version":1,"tunnels":[{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","name":"up","kind":"ncat-socks","enabled":true,"options":{"remoteHost":"example.test","listenPort":22001,"listenAddress":"127.0.0.1","socksHost":"127.0.0.1","socksPort":25560,"remotePort":22}}]}""");
            Assert.True(TunnelTransfer.TryRead(file, out var records, out _));
            Assert.True(Assert.Single(records).Enabled);

            var missing = Path.Combine(dir, "no-ver.json");
            File.WriteAllText(missing, """{"tunnels":[{"name":"a","kind":"ncat-socks","options":{"remoteHost":"example.test","listenPort":22002,"listenAddress":"127.0.0.1","socksHost":"127.0.0.1","socksPort":25560,"remotePort":22}}]}""");
            Assert.True(TunnelTransfer.TryRead(missing, out var again, out _));
            Assert.Equal("a", Assert.Single(again).Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("{not json", TunnelTransfer.NotExport)]
    [InlineData("""{"format":"tunnelman-export","version":2,"tunnels":[]}""", TunnelTransfer.NotExport)]
    [InlineData("""{"format":null,"version":1,"tunnels":[]}""", TunnelTransfer.NotExport)]
    [InlineData("""{"format":"","version":1,"tunnels":[]}""", TunnelTransfer.NotExport)]
    [InlineData("""{"format":"other","version":1,"tunnels":[]}""", TunnelTransfer.NotExport)]
    [InlineData("""{"format":"tunnelman-export","version":1,"tunnels":[]}""", TunnelTransfer.NoTunnels)]
    [InlineData("""{"format":"tunnelman-export","version":"1","tunnels":[{"name":"a"}]}""", TunnelTransfer.NotExport)]
    public void TryRead_rejects_bad_files(string json, string expected)
    {
        var dir = NewDir("tunnelman-xfer-bad-");
        try
        {
            var file = Path.Combine(dir, "x.json");
            File.WriteAllText(file, json);
            Assert.False(TunnelTransfer.TryRead(file, out _, out var error));
            Assert.Equal(expected, error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryRead_accepts_format_case()
    {
        var dir = NewDir("tunnelman-xfer-case-");
        try
        {
            var file = Path.Combine(dir, "x.json");
            File.WriteAllText(file, """{"format":"Tunnelman-Export","version":1,"tunnels":[{"name":"a","kind":"ncat-socks","options":{"remoteHost":"example.test","listenPort":22001,"listenAddress":"127.0.0.1","socksHost":"127.0.0.1","socksPort":25560,"remotePort":22}}]}""");
            Assert.True(TunnelTransfer.TryRead(file, out var records, out _));
            Assert.Equal("a", Assert.Single(records).Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PrepareImport_uniques_names_and_listens()
    {
        var existing = new[]
        {
            new TunnelRecord { Name = "Office", Kind = TunnelKinds.NcatSocks, Options = Hop(22001) },
        };
        var incoming = new[]
        {
            new TunnelRecord { Name = " Office ", Kind = TunnelKinds.NcatSocks, Options = Hop(22001) },
            new TunnelRecord { Name = "Office (2)", Kind = TunnelKinds.NcatSocks, Options = Hop(22003) },
        };

        var rows = TunnelTransfer.PrepareImport(existing, incoming);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Office (2)", rows[0].Record.Name);
        Assert.Contains("Renamed from Office", rows[0].Note, StringComparison.Ordinal);
        Assert.Contains("Listen was 127.0.0.1:22001", rows[0].Note, StringComparison.Ordinal);
        Assert.Equal(22002, PortOf(rows[0].Record));
        Assert.Equal("Office (2) (2)", rows[1].Record.Name);
        Assert.True(rows[1].CanSelect);

        var kept = Assert.Single(TunnelTransfer.PrepareImport(existing, [
            new TunnelRecord { Name = "Office (2)", Kind = TunnelKinds.NcatSocks, Options = Hop(22003) },
        ]));
        Assert.Equal("Office (2)", kept.Record.Name);
        Assert.True(kept.CanSelect);
    }

    [Fact]
    public void PrepareImport_invalid_does_not_occupy_name()
    {
        var incoming = new[]
        {
            new TunnelRecord { Name = "Office", Kind = TunnelKinds.NcatSocks, Options = new JsonObject() },
            new TunnelRecord { Name = "Office", Kind = TunnelKinds.NcatSocks, Options = Hop(22001) },
        };
        var rows = TunnelTransfer.PrepareImport([], incoming);
        Assert.False(rows[0].CanSelect);
        Assert.Equal("This entry is invalid.", rows[0].Note);
        Assert.True(rows[1].CanSelect);
        Assert.Equal("Office", rows[1].Record.Name);
    }

    [Fact]
    public void PrepareImport_unknown_null_options_and_omitted_kind()
    {
        var unknown = TunnelTransfer.PrepareImport([], [
            new TunnelRecord { Name = "future", Kind = "rathole", Options = null },
        ]);
        Assert.True(Assert.Single(unknown).CanSelect);

        var dir = NewDir("tunnelman-xfer-kind-");
        try
        {
            var file = Path.Combine(dir, "x.json");
            File.WriteAllText(file, """{"format":"tunnelman-export","version":1,"tunnels":[{"name":"plain","options":{"remoteHost":"example.test","listenPort":22001,"listenAddress":"127.0.0.1","socksHost":"127.0.0.1","socksPort":25560,"remotePort":22}}]}""");
            Assert.True(TunnelTransfer.TryRead(file, out var records, out _));
            var row = Assert.Single(TunnelTransfer.PrepareImport([], records));
            Assert.Equal(TunnelKinds.NcatSocks, row.Record.Kind);
            Assert.True(row.CanSelect);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PrepareImport_string_listen_port_is_invalid_row()
    {
        var options = new JsonObject
        {
            ["remoteHost"] = "example.test",
            ["listenPort"] = "22001",
            ["listenAddress"] = "127.0.0.1",
            ["socksHost"] = "127.0.0.1",
            ["socksPort"] = 25560,
            ["remotePort"] = 22,
        };
        var row = Assert.Single(TunnelTransfer.PrepareImport([], [
            new TunnelRecord { Name = "bad", Kind = TunnelKinds.NcatSocks, Options = options },
        ]));
        Assert.False(row.CanSelect);
    }

    [Fact]
    public void PrepareImport_keeps_extra_option_keys_on_listen_remap()
    {
        var options = Hop(22001);
        options["extra"] = "keep";
        var rows = TunnelTransfer.PrepareImport(
            [new TunnelRecord { Name = "one", Kind = TunnelKinds.NcatSocks, Options = Hop(22001) }],
            [new TunnelRecord { Name = "two", Kind = TunnelKinds.NcatSocks, Options = options }]);
        Assert.Equal("keep", rows[0].Record.Options?["extra"]?.GetValue<string>());
        Assert.Equal(JsonValueKind.Number, rows[0].Record.Options?["listenPort"]?.GetValueKind());
    }

    [Fact]
    public void BuildExport_does_not_rename()
    {
        var rows = TunnelTransfer.BuildExport([
            new TunnelRecord { Name = "Office", Kind = TunnelKinds.NcatSocks, Options = Hop(22001) },
            new TunnelRecord { Name = "Office", Kind = TunnelKinds.NcatSocks, Options = Hop(22001) },
            new TunnelRecord { Name = "  ", Kind = TunnelKinds.NcatSocks, Options = Hop(22002) },
        ]);
        Assert.Equal("Office", rows[0].Record.Name);
        Assert.Equal("Office", rows[1].Record.Name);
        Assert.True(rows[0].CanSelect);
        Assert.True(rows[1].CanSelect);
        Assert.False(rows[2].CanSelect);
        Assert.Equal("(unnamed)", rows[2].DisplayName);
    }

    [Fact]
    public void NormalizeExportPath_strips_double_json()
    {
        Assert.Equal(@"C:\out\tunnels.tunnelman.json", TunnelTransfer.NormalizeExportPath(@"C:\out\tunnels.tunnelman.json.json"));
        Assert.Equal(@"C:\out\backup.tunnelman.json", TunnelTransfer.NormalizeExportPath(@"C:\out\backup"));
        Assert.Equal(@"C:\out\a.json", TunnelTransfer.NormalizeExportPath(@"C:\out\a.json"));
    }

    private static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JsonObject Hop(int listenPort) =>
        new NcatSocksOptions { RemoteHost = "example.test", ListenPort = listenPort }.ToJson();

    private static int PortOf(TunnelRecord record)
    {
        Assert.True(NcatSocksOptions.TryParse(record.Options, out var options, out _));
        return options.ListenPort;
    }
}
