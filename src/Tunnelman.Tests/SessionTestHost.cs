using System.Windows.Threading;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

internal static class SessionTestHost
{
    public const string Passphrase = "test-passphrase";

    public static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static TunnelRecord Hop(string name, int listenPort, string listenAddress = "127.0.0.1") => new()
    {
        Name = name,
        RemoteHost = "example.test",
        RemotePort = 22,
        ListenAddress = listenAddress,
        ListenPort = listenPort,
        SocksHost = "127.0.0.1",
        SocksPort = 25560,
    };

    public static TunnelSession Open(
        string dir,
        Dispatcher dispatcher,
        Func<ITunnelRuntime>? factory = null,
        IEnumerable<TunnelRecord>? records = null)
    {
        var store = new TunnelStore(Path.Combine(dir, "tunnels.json"));
        var key = store.Create(Passphrase, iterations: 1, records?.ToList() ?? []);
        Assert.NotNull(key);
        key.Dispose();
        var session = new TunnelSession(store, dispatcher, factory);
        Assert.True(session.TryUnlock(Passphrase, out var error), error);
        return session;
    }
}
