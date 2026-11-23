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
}
