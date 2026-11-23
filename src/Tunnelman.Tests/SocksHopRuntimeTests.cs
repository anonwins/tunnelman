using System.Net;
using System.Net.Sockets;
using System.Text;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class SocksHopRuntimeTests
{
    [Fact]
    public async Task Dial_sends_domain_and_forwards_bytes_for_two_clients()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        using var listen = new TcpListener(IPAddress.Loopback, 0);
        listen.Start();
        var listenPort = ((IPEndPoint)listen.LocalEndpoint).Port;
        listen.Stop();

        var requests = new List<byte[]>();
        var proxyTask = Task.Run(() => ServeProxy(proxy, requests, 2));
        var dir = SessionTestHost.NewDir("tunnelman-hop-");
        try
        {
            var record = SessionTestHost.Hop("hop", listenPort);
            record.SocksPort = proxyPort;
            var store = new TunnelStore(Path.Combine(dir, "tunnels.json"));
            var key = store.Create(SessionTestHost.Passphrase, 1, []);
            Assert.NotNull(key);
            var runtime = new SocksHopRuntime();
            var lines = new List<string>();
            await runtime.StartAsync(record, key, lines.Add, CancellationToken.None);
            Assert.Contains("Listening", lines);

            await RoundTrip(listenPort, "one");
            await RoundTrip(listenPort, "two");
            await proxyTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, requests.Count);
            foreach (var request in requests)
            {
                Assert.Equal(0x05, request[0]);
                Assert.Equal(0x01, request[1]);
                Assert.Equal(0x03, request[3]);
                var length = request[4];
                var host = Encoding.ASCII.GetString(request, 5, length);
                Assert.Equal("example.test", host);
            }

            await runtime.StopAsync();
            key.Dispose();
        }
        finally
        {
            proxy.Stop();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Direct_dial_forwards_payload_without_a_socks_greeting()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        using var listen = new TcpListener(IPAddress.Loopback, 0);
        listen.Start();
        var listenPort = ((IPEndPoint)listen.LocalEndpoint).Port;
        listen.Stop();

        var seen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var echoTask = Task.Run(async () =>
        {
            using var client = await echo.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[8];
            var read = await stream.ReadAsync(buffer);
            var payload = buffer[..read];
            seen.TrySetResult(payload);
            await stream.WriteAsync(payload);
        });

        var dir = SessionTestHost.NewDir("tunnelman-direct-");
        try
        {
            var record = SessionTestHost.Hop("direct", listenPort);
            record.SocksHost = "";
            record.SocksPort = 0;
            record.RemoteHost = "127.0.0.1";
            record.RemotePort = echoPort;
            var store = new TunnelStore(Path.Combine(dir, "tunnels.json"));
            var key = store.Create(SessionTestHost.Passphrase, 1, []);
            Assert.NotNull(key);
            var runtime = new SocksHopRuntime();
            await runtime.StartAsync(record, key, _ => { }, CancellationToken.None);
            await RoundTrip(listenPort, "direct");
            var first = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("direct"u8.ToArray(), first);
            await echoTask.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.StopAsync();
            key.Dispose();
        }
        finally
        {
            echo.Stop();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task RoundTrip(int port, string payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(payload);
        await stream.WriteAsync(bytes);
        var read = new byte[bytes.Length];
        var offset = 0;
        while (offset < read.Length)
        {
            var n = await stream.ReadAsync(read.AsMemory(offset));
            if (n == 0)
                break;
            offset += n;
        }

        Assert.Equal(payload, Encoding.ASCII.GetString(read));
    }

    private static async Task ServeProxy(TcpListener listener, List<byte[]> requests, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var greeting = new byte[3];
            await ReadExact(stream, greeting);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });
            var head = new byte[5];
            await ReadExact(stream, head);
            var host = new byte[head[4]];
            await ReadExact(stream, host);
            var port = new byte[2];
            await ReadExact(stream, port);
            var request = new byte[head.Length + host.Length + port.Length];
            head.CopyTo(request, 0);
            host.CopyTo(request, head.Length);
            port.CopyTo(request, head.Length + host.Length);
            lock (requests)
                requests.Add(request);
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            var buffer = new byte[32];
            var read = await stream.ReadAsync(buffer);
            if (read > 0)
                await stream.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static async Task ReadExact(NetworkStream stream, Memory<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..]);
            if (n == 0)
                throw new IOException("closed");
            read += n;
        }
    }
}
