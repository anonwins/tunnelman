using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Tunnelman.Tunnels;

internal sealed class SocksHopRuntime : ITunnelRuntime
{
    private readonly object _gate = new();
    private readonly List<TcpClient> _clients = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _accept;
    private byte[]? _sealed;
    private IDialSecrets? _secrets;
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

    public Task StartAsync(TunnelRecord record, IDialSecrets secrets, Action<string> log, CancellationToken cancellationToken)
    {
        var endpoint = ResolveListen(record.ListenAddress, record.ListenPort);
        var listener = new TcpListener(endpoint);
        try
        {
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            listener.Server.Dispose();
            throw new InvalidOperationException($"Could not listen on {record.ListenAddress.Trim()}:{record.ListenPort}");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _listener = listener;
            _cts = cts;
            _secrets = secrets;
            _sealed = secrets.Seal(DialBlob.Encode(record));
            _stopping = false;
            _accept = Task.Run(() => AcceptLoop(listener, log, cts.Token));
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
        byte[]? sealedBlob;
        IDialSecrets? secrets;
        lock (_gate)
        {
            sealedBlob = _sealed;
            secrets = _secrets;
        }

        if (sealedBlob is null || secrets is null)
            return null;

        if (!TryOpenDial(secrets, sealedBlob, out var socksHost, out var socksPort, out var remoteHost, out var remoteCopy, out var remotePort))
        {
            log("Connection failed");
            return null;
        }

        var direct = socksHost.Length == 0 || socksPort == 0;
        try
        {
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

                    request = BuildConnect(remoteHost, remoteCopy, remotePort);
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
                    CryptographicOperations.ZeroMemory(request);
                CryptographicOperations.ZeroMemory(remoteCopy);
            }

            return proxy;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(remoteCopy);
        }
    }

    private static bool TryOpenDial(
        IDialSecrets secrets,
        byte[] sealedBlob,
        out string socksHost,
        out int socksPort,
        out string remoteHost,
        out byte[] remoteCopy,
        out int remotePort)
    {
        socksHost = "";
        socksPort = 0;
        remoteHost = "";
        remoteCopy = [];
        remotePort = 0;
        var plainLength = sealedBlob.Length - TunnelCipher.NonceBytes - TunnelCipher.TagBytes;
        if (plainLength < 1)
            return false;
        var rented = ArrayPool<byte>.Shared.Rent(plainLength);
        try
        {
            secrets.Open(sealedBlob, rented.AsSpan(0, plainLength));
            if (!DialBlob.TryRead(rented.AsSpan(0, plainLength), out socksHost, out socksPort, out remoteHost, out var remoteBytes, out remotePort))
                return false;
            remoteCopy = remoteBytes.ToArray();
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented.AsSpan(0, plainLength));
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static byte[] BuildConnect(string remoteHost, byte[] remoteBytes, int remotePort)
    {
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
            _secrets = null;
            if (_sealed is not null)
            {
                CryptographicOperations.ZeroMemory(_sealed);
                _sealed = null;
            }

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
}

internal static class DialBlob
{
    public static byte[] Encode(TunnelRecord record)
    {
        var socks = Encoding.UTF8.GetBytes(record.SocksHost.Trim());
        var remote = Encoding.UTF8.GetBytes(record.RemoteHost.Trim());
        var blob = new byte[1 + socks.Length + 2 + 1 + remote.Length + 2];
        var offset = 0;
        blob[offset++] = (byte)socks.Length;
        socks.CopyTo(blob.AsSpan(offset));
        offset += socks.Length;
        blob[offset++] = (byte)(record.SocksPort >> 8);
        blob[offset++] = (byte)record.SocksPort;
        blob[offset++] = (byte)remote.Length;
        remote.CopyTo(blob.AsSpan(offset));
        offset += remote.Length;
        blob[offset++] = (byte)(record.RemotePort >> 8);
        blob[offset] = (byte)record.RemotePort;
        CryptographicOperations.ZeroMemory(socks);
        CryptographicOperations.ZeroMemory(remote);
        return blob;
    }

    public static bool TryRead(
        ReadOnlySpan<byte> data,
        out string socksHost,
        out int socksPort,
        out string remoteHost,
        out ReadOnlySpan<byte> remoteBytes,
        out int remotePort)
    {
        socksHost = "";
        socksPort = 0;
        remoteHost = "";
        remoteBytes = default;
        remotePort = 0;
        if (data.Length < 1)
            return false;
        var socksLength = data[0];
        if (data.Length < 1 + socksLength + 2 + 1)
            return false;
        var socks = data.Slice(1, socksLength);
        var offset = 1 + socksLength;
        socksPort = (data[offset] << 8) | data[offset + 1];
        offset += 2;
        var remoteLength = data[offset++];
        if (data.Length < offset + remoteLength + 2)
            return false;
        remoteBytes = data.Slice(offset, remoteLength);
        offset += remoteLength;
        remotePort = (data[offset] << 8) | data[offset + 1];
        socksHost = Encoding.UTF8.GetString(socks);
        remoteHost = Encoding.UTF8.GetString(remoteBytes);
        var direct = socksLength == 0 && socksPort == 0;
        var proxied = socksLength > 0 && socksPort is >= 1 and <= 65535;
        return (direct || proxied) && remotePort is >= 1 and <= 65535 && remoteLength > 0;
    }
}
