using System.Text.Json.Nodes;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class NcatCommandBuilderTests
{
    private static NcatSocksOptions Sample() => new()
    {
        ListenAddress = "127.0.0.1",
        ListenPort = 22001,
        SocksHost = "127.0.0.1",
        SocksPort = 25560,
        RemoteHost = "12.34.56.78",
        RemotePort = 22,
    };

    [Fact]
    public void BuildOuterArguments_uses_exec_and_keeps_arguments_empty()
    {
        var ncat = @"C:\tools\ncat.exe";
        var args = NcatCommandBuilder.BuildOuterArguments(ncat, Sample());
        var psi = NcatCommandBuilder.CreateStartInfo(ncat, args);

        Assert.Equal("", psi.Arguments);
        Assert.Equal(ncat, psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(7, args.Count);
        Assert.Equal("-v", args[0]);
        Assert.Equal("-l", args[1]);
        Assert.Equal("127.0.0.1", args[2]);
        Assert.Equal("22001", args[3]);
        Assert.Equal("--keep-open", args[4]);
        Assert.Equal("--exec", args[5]);
        Assert.Equal(@"C:\tools\ncat.exe -v --proxy 127.0.0.1:25560 --proxy-type socks5 12.34.56.78 22", args[6]);
    }

    [Fact]
    public void BuildExecCommand_quotes_path_with_spaces()
    {
        var ncat = @"C:\Program Files (x86)\Nmap\ncat.exe";
        var exec = NcatCommandBuilder.BuildExecCommand(ncat, Sample());
        Assert.StartsWith("\"C:\\Program Files (x86)\\Nmap\\ncat.exe\"", exec, StringComparison.Ordinal);
        Assert.Contains("--proxy 127.0.0.1:25560", exec, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad host")]
    [InlineData("host&more")]
    [InlineData("host|x")]
    [InlineData("")]
    public void Rejects_bad_hosts(string host)
    {
        Assert.False(NcatCommandBuilder.TryValidateHost(host, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Accepts_localhost_and_ipv4()
    {
        Assert.True(NcatCommandBuilder.TryValidateHost("localhost", out _));
        Assert.True(NcatCommandBuilder.TryValidateHost("127.0.0.1", out _));
        Assert.True(NcatCommandBuilder.TryValidateHost("0.0.0.0", out _));
    }

    [Fact]
    public void Endpoint_parts_format_listen_remote_proxy()
    {
        var o = Sample();
        Assert.Equal("127.0.0.1:22001", o.ListenText);
        Assert.Equal("12.34.56.78:22", o.RemoteText);
        Assert.Equal("SOCKS5 127.0.0.1:25560", o.ProxyText);
        Assert.True(o.HasProxy);
    }

    [Fact]
    public void NcatSocksKind_validates_options()
    {
        var kind = new NcatSocksKind();
        Assert.True(kind.TryValidate(Sample().ToJson(), out _));
        Assert.False(kind.TryValidate(new JsonObject { ["remoteHost"] = "nope&" }, out _));
    }
}
