using System.Text.Json.Nodes;
using System.Windows.Threading;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelSessionTests
{
    [Fact]
    public void Load_restores_enabled_flags_without_starting_when_ncat_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-session-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var store = new TunnelStore(file);
            store.Save([
                new TunnelRecord
                {
                    Id = Guid.NewGuid(),
                    Name = "up",
                    Kind = TunnelKinds.NcatSocks,
                    Enabled = true,
                    Options = new NcatSocksOptions { RemoteHost = "example.test", ListenPort = 22001 }.ToJson(),
                },
                new TunnelRecord
                {
                    Id = Guid.NewGuid(),
                    Name = "down",
                    Kind = TunnelKinds.NcatSocks,
                    Enabled = false,
                    Options = new NcatSocksOptions { RemoteHost = "example.test", ListenPort = 22002 }.ToJson(),
                },
            ]);

            var session = new TunnelSession(store, Dispatcher.CurrentDispatcher);
            session.Load();
            Assert.Equal(2, session.Tunnels.Count);
            Assert.True(session.Tunnels[0].Enabled);
            Assert.False(session.Tunnels[1].Enabled);
            Assert.Equal(0, session.RunningCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryAdd_rejects_duplicate_listen_and_persists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-session-add-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            var session = new TunnelSession(new TunnelStore(file), Dispatcher.CurrentDispatcher);
            session.Load();
            var options = new NcatSocksOptions { RemoteHost = "example.test", ListenPort = 22001 }.ToJson();
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
}
