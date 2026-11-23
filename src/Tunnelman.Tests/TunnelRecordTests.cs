using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelRecordTests
{
    [Theory]
    [InlineData("bad host")]
    [InlineData("host&more")]
    [InlineData("host|x")]
    [InlineData("")]
    public void Rejects_bad_hosts(string host)
    {
        Assert.False(TunnelRecord.TryValidateHost(host, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Accepts_localhost_and_ipv4()
    {
        Assert.True(TunnelRecord.TryValidateHost("localhost", out _));
        Assert.True(TunnelRecord.TryValidateHost("127.0.0.1", out _));
        Assert.True(TunnelRecord.TryValidateHost("0.0.0.0", out _));
    }

    [Fact]
    public void Endpoint_parts_format_listen_remote_proxy()
    {
        var record = new TunnelRecord
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 22001,
            SocksHost = "127.0.0.1",
            SocksPort = 25560,
            RemoteHost = "12.34.56.78",
            RemotePort = 22,
        };
        Assert.Equal("127.0.0.1:22001", record.ListenText);
        Assert.Equal("12.34.56.78:22", record.RemoteText);
        Assert.Equal("SOCKS5 127.0.0.1:25560", record.ProxyText);
        Assert.True(record.HasProxy);
        Assert.True(record.TryValidateEndpoints(out _));
    }

    [Fact]
    public void Empty_socks_is_valid_and_not_a_proxy()
    {
        var record = new TunnelRecord
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 22001,
            RemoteHost = "example.test",
            RemotePort = 22,
        };
        Assert.False(record.HasProxy);
        Assert.True(record.TryValidateEndpoints(out _));
    }

    [Theory]
    [InlineData("127.0.0.1", 0)]
    [InlineData("", 25560)]
    public void Half_filled_socks_is_rejected(string host, int port)
    {
        var record = new TunnelRecord
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 22001,
            SocksHost = host,
            SocksPort = port,
            RemoteHost = "example.test",
            RemotePort = 22,
        };
        Assert.False(record.TryValidateEndpoints(out var error));
        Assert.Equal(TunnelRecord.SocksPairRequired, error);
    }
}
