using System.Text.Json;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelTransferTests
{
    [Fact]
    public void Write_then_read_export_keeps_enabled()
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
            Assert.Contains("example.test", text, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(TunnelTransfer.FormatName, root.GetProperty("format").GetString());
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal(TimeSpan.Zero, root.GetProperty("exportedAt").GetDateTimeOffset().Offset);
            Assert.True(root.GetProperty("tunnels")[0].GetProperty("enabled").GetBoolean());

            var opened = TunnelTransfer.Read(file);
            Assert.Equal(ExportReadStatus.Ok, opened.Status);
            var loaded = Assert.Single(opened.Tunnels);
            Assert.Equal(record.Id, loaded.Id);
            Assert.Equal("office", loaded.Name);
            Assert.True(loaded.Enabled);
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
    [InlineData("""{"format":"tunnelman-export","version":"1","tunnels":[{"name":"a"}]}""", TunnelTransfer.NotExport)]
    [InlineData("""{"version":1,"kdf":"pbkdf2-sha256","ciphertext":"AAAA","tunnels":[]}""", TunnelTransfer.NotExport)]
    public void TryRead_rejects_bad_files(string json, string expected)
    {
        var dir = SessionTestHost.NewDir("tunnelman-xfer-bad-");
        try
        {
            var file = Path.Combine(dir, "x.json");
            File.WriteAllText(file, json);
            var read = TunnelTransfer.Read(file);
            Assert.NotEqual(ExportReadStatus.Ok, read.Status);
            Assert.Equal(expected, read.Error);
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
            var read = TunnelTransfer.Read(file);
            Assert.Equal(ExportReadStatus.NotExport, read.Status);
            Assert.Equal(TunnelTransfer.NotExport, read.Error);
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
            File.WriteAllText(file, """{"format":"Tunnelman-Export","version":1,"exportedAt":"2026-01-01T00:00:00Z","tunnels":[{"name":"a","enabled":true,"remoteHost":"example.test","listenPort":22001,"listenAddress":"127.0.0.1","socksHost":"127.0.0.1","socksPort":25560,"remotePort":22}]}""");
            var read = TunnelTransfer.Read(file);
            Assert.Equal(ExportReadStatus.Ok, read.Status);
            var loaded = Assert.Single(read.Tunnels);
            Assert.Equal("a", loaded.Name);
            Assert.True(loaded.Enabled);
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

    [Fact]
    public void Empty_export_has_no_tunnels()
    {
        var dir = SessionTestHost.NewDir("tunnelman-xfer-empty-");
        try
        {
            var file = Path.Combine(dir, "x.json");
            File.WriteAllText(file, """{"format":"tunnelman-export","version":1,"tunnels":[]}""");
            var read = TunnelTransfer.Read(file);
            Assert.Equal(ExportReadStatus.NoTunnels, read.Status);
            Assert.Equal(TunnelTransfer.NoTunnels, read.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Session_file_is_not_an_export_and_export_on_session_path_is_foreign()
    {
        var dir = SessionTestHost.NewDir("tunnelman-xfer-session-");
        try
        {
            var sessionPath = Path.Combine(dir, "tunnels.json");
            var store = new TunnelStore(sessionPath);
            store.Save([SessionTestHost.Hop("office", 22001)]);
            var sessionText = File.ReadAllText(sessionPath);
            using (var sessionDoc = JsonDocument.Parse(sessionText))
            {
                Assert.False(sessionDoc.RootElement.TryGetProperty("format", out _));
                Assert.False(sessionDoc.RootElement.TryGetProperty("purpose", out _));
            }

            var read = TunnelTransfer.Read(sessionPath);
            Assert.Equal(ExportReadStatus.NotExport, read.Status);

            var beforeSession = File.ReadAllBytes(sessionPath);
            TunnelTransfer.Write(sessionPath, [SessionTestHost.Hop("office", 22001)]);
            var exportStore = new TunnelStore(sessionPath);
            Assert.Equal(TunnelProbeKind.Foreign, exportStore.Probe().Kind);
            var exportBytes = File.ReadAllBytes(sessionPath);
            Assert.Equal(exportBytes, File.ReadAllBytes(sessionPath));
            Assert.NotEqual(beforeSession, exportBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
