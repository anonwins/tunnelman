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

    public static TestMaterial CreateKey()
    {
        var dir = NewDir("tunnelman-key-");
        var store = new TunnelStore(Path.Combine(dir, "tunnels.json"));
        var key = store.Create(Passphrase, iterations: 1, []);
        Assert.NotNull(key);
        Assert.True(store.TryCopyMaterial(out var salt, out var iterations));
        Assert.Equal(1, iterations);
        return new TestMaterial(key, salt, iterations, dir);
    }

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

    internal sealed class TestMaterial(SessionKey key, byte[] salt, int iterations, string directory) : IDisposable
    {
        public SessionKey Key { get; } = key;
        public byte[] Salt { get; } = salt;
        public int Iterations { get; } = iterations;
        public string Directory { get; } = directory;

        public void Dispose()
        {
            Key.Dispose();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
