using System.Security.Cryptography;
using System.Text;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelCipherTests
{
    [Fact]
    public void Dial_key_opens_its_blob_and_not_the_session_file()
    {
        using var material = SessionTestHost.CreateKey();
        var plain = Encoding.UTF8.GetBytes("{\"tunnels\":[]}");
        var file = material.Key.Encrypt(plain, out var nonce);
        var dial = material.Key.DeriveDial();
        var blob = dial.Seal(plain);
        var opened = new byte[plain.Length];
        dial.Open(blob, opened);
        Assert.Equal(plain, opened);
        Assert.Throws<AuthenticationTagMismatchException>(() => dial.Decrypt(nonce, file));
    }
}
