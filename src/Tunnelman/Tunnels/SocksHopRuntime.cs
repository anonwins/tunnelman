using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tunnelman.Tunnels;

internal sealed class SocksHopRuntime : ITunnelRuntime
{
    private readonly object _gate = new();
    private readonly List<TcpClient> _clients = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _accept;
    private DialEndpoint? _target;
    private bool _stopping;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _listener is not null && !_stopping;
        }
    }

    public event Action<int>? Exited;

    public Task StartAsync(TunnelRecord record, Action<string> log, CancellationToken cancellationToken)
    {
        TcpListener? listener = null;
        var owned = false;
        var target = DialEndpoint.Copy(record);
        try
        {
            var endpoint = ResolveListen(record.ListenAddress, record.ListenPort);
            listener = new TcpListener(endpoint);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate)
            {
                _listener = listener;
                _cts = cts;
                _target = target;
                owned = true;
                listener = null;
                _stopping = false;
                _accept = Task.Run(() => AcceptLoop(_listener!, log, cts.Token));
            }
        }
        catch (Exception ex) when (!owned && (ex is SocketException or InvalidOperationException))
        {
            listener?.Server.Dispose();
            throw new InvalidOperationException($"Could not listen on {record.ListenAddress.Trim()}:{record.ListenPort}");
        }
        catch
        {
            if (!owned && listener is not null)
            {
                try
                {
                    listener.Stop();
                }
                catch (SocketException)
                {
                }

                listener.Server.Dispose();
            }

            throw;
        }

        log("Listening");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? accept;
        lock (_gate)
        {
            _stopping = true;
            accept = _accept;
        }

        Shutdown();
        if (accept is not null)
        {
            try
            {
                await accept.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _stopping = true;
        Shutdown();
    }

    private async Task AcceptLoop(TcpListener listener, Action<string> log, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
                {
                    break;
                }

                lock (_gate)
                    _clients.Add(client);
                var relay = RelayAsync(client, log, cancellationToken);
                _ = relay.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        _ = task.Exception;
                    lock (_gate)
                        _clients.Remove(client);
                    client.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
        finally
        {
            bool stopping;
            lock (_gate)
                stopping = _stopping;
            if (!stopping)
                Exited?.Invoke(1);
        }
    }

    private async Task RelayAsync(TcpClient client, Action<string> log, CancellationToken cancellationToken)
    {
        TcpClient? proxy = null;
        try
        {
            proxy = await DialAsync(log, cancellationToken).ConfigureAwait(false);
            if (proxy is null)
            {
                client.Dispose();
                return;
            }

            log("Connection opened");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var left = client.GetStream();
            var right = proxy.GetStream();
            var toProxy = PumpAsync(left, right, linked.Token);
            var toClient = PumpAsync(right, left, linked.Token);
            var finished = await Task.WhenAny(toProxy, toClient).ConfigureAwait(false);
            linked.Cancel();
            try
            {
                await finished.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }

            log("Connection closed");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            log("Connection closed");
        }
        finally
        {
            proxy?.Dispose();
            client.Dispose();
        }
    }

    private async Task<TcpClient?> DialAsync(Action<string> log, CancellationToken cancellationToken)
    {
        DialEndpoint? target;
        lock (_gate)
            target = _target;

        if (target is null)
            return null;

        return await DialOpenedAsync(target, log, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TcpClient?> DialOpenedAsync(
        DialEndpoint target,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var socksHost = target.SocksHost;
        var socksPort = target.SocksPort;
        var remoteHost = target.RemoteHost;
        var remotePort = target.RemotePort;
        var direct = socksHost.Length == 0 || socksPort == 0;
        var proxy = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        byte[]? request = null;
        try
        {
            if (direct)
            {
                await proxy.ConnectAsync(remoteHost, remotePort, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                await proxy.ConnectAsync(socksHost, socksPort, timeout.Token).ConfigureAwait(false);
                var stream = proxy.GetStream();
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, timeout.Token).ConfigureAwait(false);
                var method = new byte[2];
                await ReadExactAsync(stream, method, timeout.Token).ConfigureAwait(false);
                if (method[0] != 0x05 || method[1] != 0x00)
                {
                    proxy.Dispose();
                    log("Proxy requires authentication");
                    return null;
                }

                request = BuildConnect(remoteHost, remotePort);
                await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
                if (!await ReadReplyAsync(stream, timeout.Token).ConfigureAwait(false))
                {
                    proxy.Dispose();
                    log("Proxy refused the connection");
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
        {
            proxy.Dispose();
            log(direct ? "Connection failed" : "Proxy connection failed");
            return null;
        }
        finally
        {
            if (request is not null)
                Array.Clear(request);
        }

        return proxy;
    }

    private static byte[] BuildConnect(string remoteHost, int remotePort)
    {
        var remoteBytes = Encoding.UTF8.GetBytes(remoteHost);
        var ipv4 = TunnelRecord.IsIPv4(remoteHost);
        var addressLength = ipv4 ? 4 : 1 + remoteBytes.Length;
        var request = new byte[4 + addressLength + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        var offset = 4;
        if (ipv4)
        {
            request[3] = 0x01;
            var parts = remoteHost.Split('.');
            for (var i = 0; i < 4; i++)
                request[offset++] = (byte)int.Parse(parts[i]);
        }
        else
        {
            request[3] = 0x03;
            request[offset++] = (byte)remoteBytes.Length;
            remoteBytes.AsSpan().CopyTo(request.AsSpan(offset));
            offset += remoteBytes.Length;
        }

        request[offset++] = (byte)(remotePort >> 8);
        request[offset] = (byte)remotePort;
        return request;
    }

    private static async Task<bool> ReadReplyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (header[0] != 0x05 || header[1] != 0x00)
            return false;

        var extra = header[3] switch
        {
            0x01 => 4 + 2,
            0x04 => 16 + 2,
            0x03 => -1,
            _ => -2,
        };
        if (extra == -2)
            return false;
        if (extra < 0)
        {
            var length = new byte[1];
            await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false);
            extra = length[0] + 2;
        }

        var bound = new byte[extra];
        await ReadExactAsync(stream, bound, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await from.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    break;
                }

                if (read == 0)
                    break;
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
                throw new IOException("The proxy closed the connection.");
            read += n;
        }
    }

    private static IPEndPoint ResolveListen(string host, int port)
    {
        var trimmed = host.Trim();
        if (IPAddress.TryParse(trimmed, out var address))
            return new IPEndPoint(address, port);
        var matches = Dns.GetHostAddresses(trimmed);
        var v4 = matches.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
        if (v4 is null)
            throw new InvalidOperationException($"Could not listen on {trimmed}:{port}");
        return new IPEndPoint(v4, port);
    }

    private void Shutdown()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        TcpClient[] clients;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
            _target = null;

            clients = _clients.ToArray();
            _clients.Clear();
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            listener?.Stop();
        }
        catch (SocketException)
        {
        }

        foreach (var client in clients)
            client.Dispose();
        cts?.Dispose();
    }

    private sealed class DialEndpoint
    {
        public required string SocksHost { get; init; }
        public required int SocksPort { get; init; }
        public required string RemoteHost { get; init; }
        public required int RemotePort { get; init; }

        public static DialEndpoint Copy(TunnelRecord record) => new()
        {
            SocksHost = record.SocksHost.Trim(),
            SocksPort = record.SocksPort,
            RemoteHost = record.RemoteHost.Trim(),
            RemotePort = record.RemotePort,
        };
    }
}
