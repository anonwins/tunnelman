using System.Text.Json;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelTransferTests
{
    [Fact]
    public void Write_then_read_export_v1_omits_enabled()
    {
        var dir = SessionTestHost.NewDir("tunnelman-xfer-write-");
        try
        {
            var file = Path.Combine(dir, "tunnels.tunnelman.json");
            var record = SessionTestHost.Hop("office", 22001);
            record.Id = Guid.NewGuid();
            record.Enabled = true;
            TunnelTransfer.Write(file, [record]);

            var text = File.ReadAllText(file);
            Assert.Contains("\"format\":", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"enabled\"", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"kind\"", text, StringComparison.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(TunnelTransfer.FormatName, root.GetProperty("format").GetString());
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal(TimeSpan.Zero, root.GetProperty("exportedAt").GetDateTimeOffset().Offset);

            Assert.True(TunnelTransfer.TryRead(file, out var records, out var error));
            Assert.Equal("", error);
            var loaded = Assert.Single(records);
            Assert.Equal(record.Id, loaded.Id);
            Assert.Equal("office", loaded.Name);
            Assert.False(loaded.Enabled);
            Assert.Equal("example.test", loaded.RemoteHost);
            Assert.Equal(22001, loaded.ListenPort);
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
        var dir = SessionTestHost.NewDir("tunnelman-xfer-bad-");
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
    public void TryRead_string_listen_port_rejects_the_file()
    {
        var dir = SessionTestHost.NewDir("tunnelman-xfer-port-");
        try
        {
            var file = Path.Combine(dir, "x.json");
            File.WriteAllText(file, """{"format":"tunnelman-export","version":1,"tunnels":[{"name":"bad","remoteHost":"example.test","listenPort":"22001","listenAddress":"127.0.0.1","socksHost":"127.0.0.1","socksPort":25560,"remotePort":22}]}""");
            Assert.False(TunnelTransfer.TryRead(file, out _, out var error));
            Assert.Equal(TunnelTransfer.NotExport, error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryRead_accepts_format_case()
    {
        var dir = SessionTestHost.NewDir("tunnelman-xfer-case-");
        try
        {
            var file = Path.Combine(dir, "x.json");
            File.WriteAllText(file, """{"format":"Tunnelman-Export","version":1,"tunnels":[{"name":"a","remoteHost":"example.test","listenPort":22001,"listenAddress":"127.0.0.1","socksHost":"127.0.0.1","socksPort":25560,"remotePort":22}]}""");
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
        var existing = new[] { SessionTestHost.Hop("Office", 22001) };
        var incoming = new[]
        {
            SessionTestHost.Hop(" Office ", 22001),
            SessionTestHost.Hop("Office (2)", 22003),
        };

        var rows = TunnelTransfer.PrepareImport(existing, incoming);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Office (2)", rows[0].Record.Name);
        Assert.Contains("Renamed from Office", rows[0].Note, StringComparison.Ordinal);
        Assert.Contains("Listen was 127.0.0.1:22001", rows[0].Note, StringComparison.Ordinal);
        Assert.Equal(22002, rows[0].Record.ListenPort);
        Assert.Equal("Office (2) (2)", rows[1].Record.Name);
        Assert.True(rows[1].CanSelect);

        var kept = Assert.Single(TunnelTransfer.PrepareImport(existing, [SessionTestHost.Hop("Office (2)", 22003)]));
        Assert.Equal("Office (2)", kept.Record.Name);
        Assert.True(kept.CanSelect);
    }

    [Fact]
    public void PrepareImport_invalid_does_not_occupy_name()
    {
        var invalid = SessionTestHost.Hop("Office", 22001);
        invalid.RemoteHost = "";
        var rows = TunnelTransfer.PrepareImport([], [invalid, SessionTestHost.Hop("Office", 22001)]);
        Assert.False(rows[0].CanSelect);
        Assert.Equal("This entry is invalid.", rows[0].Note);
        Assert.True(rows[1].CanSelect);
        Assert.Equal("Office", rows[1].Record.Name);
    }

    [Fact]
    public void BuildExport_does_not_rename()
    {
        var rows = TunnelTransfer.BuildExport([
            SessionTestHost.Hop("Office", 22001),
            SessionTestHost.Hop("Office", 22001),
            SessionTestHost.Hop("  ", 22002),
        ]);
        Assert.Equal("Office", rows[0].Record.Name);
        Assert.Equal("Office", rows[1].Record.Name);
        Assert.True(rows[0].CanSelect);
        Assert.True(rows[1].CanSelect);
        Assert.False(rows[2].CanSelect);
        Assert.Equal("(unnamed)", rows[2].DisplayName);
    }

    [Fact]
    public void PrepareImport_subset_keeps_original_office_listen()
    {
        var existing = new[] { SessionTestHost.Hop("Office", 22001) };
        var incoming = new[]
        {
            SessionTestHost.Hop("Office", 22001),
            SessionTestHost.Hop("Office (2)", 22003),
        };
        var preview = TunnelTransfer.PrepareImport(existing, incoming);
        Assert.Equal("Office (2)", preview[0].Record.Name);
        Assert.Equal(22002, preview[0].Record.ListenPort);
        Assert.Equal("Office (2) (2)", preview[1].Record.Name);

        var subset = TunnelTransfer.PrepareImport(existing, [preview[1].Source]);
        var onlyB = Assert.Single(subset);
        Assert.Equal("Office (2)", onlyB.Record.Name);
        Assert.Equal(22003, onlyB.Record.ListenPort);
        Assert.Equal("Office", existing[0].Name);
        Assert.Equal(22001, existing[0].ListenPort);
    }

    [Fact]
    public void IsReservedSessionPath_covers_session_siblings()
    {
        var tunnels = AppPaths.TunnelsFile;
        Assert.True(TunnelTransfer.IsReservedSessionPath(tunnels));
        Assert.True(TunnelTransfer.IsReservedSessionPath(tunnels + ".bak"));
        Assert.True(TunnelTransfer.IsReservedSessionPath(tunnels + ".tmp"));
        Assert.True(TunnelTransfer.IsReservedSessionPath(tunnels + "." + Guid.NewGuid().ToString("n") + ".tmp"));
        Assert.True(TunnelTransfer.IsReservedSessionPath(tunnels + "." + Guid.NewGuid().ToString("n") + ".prev"));
        Assert.True(TunnelTransfer.IsReservedSessionPath(AppPaths.UiFile));
        Assert.False(TunnelTransfer.IsReservedSessionPath(@"C:\other\tunnels.json"));
    }

    [Fact]
    public void PrepareImport_unspecified_listen_overlaps_any_host()
    {
        var existing = new[] { SessionTestHost.Hop("any", 22001, "0.0.0.0") };
        var row = Assert.Single(TunnelTransfer.PrepareImport(existing, [SessionTestHost.Hop("local", 22001)]));
        Assert.True(row.CanSelect);
        Assert.Equal(22002, row.Record.ListenPort);
        Assert.Contains("Listen was", row.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeExportPath_strips_double_json()
    {
        Assert.Equal(@"C:\out\tunnels.tunnelman.json", TunnelTransfer.NormalizeExportPath(@"C:\out\tunnels.tunnelman.json.json"));
        Assert.Equal(@"C:\out\backup.tunnelman.json", TunnelTransfer.NormalizeExportPath(@"C:\out\backup"));
        Assert.Equal(@"C:\out\a.json", TunnelTransfer.NormalizeExportPath(@"C:\out\a.json"));
    }
}
