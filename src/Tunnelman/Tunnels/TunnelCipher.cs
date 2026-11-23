using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Tunnelman.Tunnels;

internal interface IDialSecrets
{
    byte[] Seal(ReadOnlySpan<byte> plaintext);

    void Open(ReadOnlySpan<byte> sealedBlob, Span<byte> destination);
}

internal sealed class SessionKey : IDialSecrets, IDisposable
{
    private readonly object _gate = new();
    private byte[]? _key;
    private GCHandle _pin;

    private SessionKey(byte[] key)
    {
        _key = key;
        _pin = GCHandle.Alloc(_key, GCHandleType.Pinned);
    }

    public static SessionKey Derive(string passphrase, ReadOnlySpan<byte> salt, int iterations)
    {
        var password = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            var key = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                TunnelCipher.KeyBytes);
            return new SessionKey(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(TunnelCipher.NonceBytes);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TunnelCipher.TagBytes];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            using var aes = new AesGcm(_key, TunnelCipher.TagBytes);
            aes.Encrypt(nonce, plaintext, cipher, tag, TunnelCipher.AssociatedData);
        }

        var blob = new byte[nonce.Length + cipher.Length + tag.Length];
        nonce.CopyTo(blob, 0);
        cipher.CopyTo(blob.AsSpan(nonce.Length));
        tag.CopyTo(blob.AsSpan(nonce.Length + cipher.Length));
        return blob;
    }

    public void Open(ReadOnlySpan<byte> sealedBlob, Span<byte> destination)
    {
        if (sealedBlob.Length < TunnelCipher.NonceBytes + TunnelCipher.TagBytes)
            throw new CryptographicException("Sealed dial data is too short.");
        var nonce = sealedBlob[..TunnelCipher.NonceBytes];
        var tag = sealedBlob[^TunnelCipher.TagBytes..];
        var cipher = sealedBlob[TunnelCipher.NonceBytes..^TunnelCipher.TagBytes];
        if (destination.Length != cipher.Length)
            throw new ArgumentException("Destination length does not match the sealed dial data.", nameof(destination));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            using var aes = new AesGcm(_key, TunnelCipher.TagBytes);
            aes.Decrypt(nonce, cipher, tag, destination, TunnelCipher.AssociatedData);
        }
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, out byte[] nonce)
    {
        nonce = RandomNumberGenerator.GetBytes(TunnelCipher.NonceBytes);
        var cipher = new byte[plaintext.Length + TunnelCipher.TagBytes];
        var tag = cipher.AsSpan(plaintext.Length);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            using var aes = new AesGcm(_key, TunnelCipher.TagBytes);
            aes.Encrypt(nonce, plaintext, cipher.AsSpan(0, plaintext.Length), tag, TunnelCipher.AssociatedData);
        }

        return cipher;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> cipherAndTag)
    {
        if (nonce.Length != TunnelCipher.NonceBytes || cipherAndTag.Length < TunnelCipher.TagBytes)
            throw new CryptographicException("Session ciphertext is too short.");
        var plain = new byte[cipherAndTag.Length - TunnelCipher.TagBytes];
        var cipher = cipherAndTag[..^TunnelCipher.TagBytes];
        var tag = cipherAndTag[^TunnelCipher.TagBytes..];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            using var aes = new AesGcm(_key, TunnelCipher.TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain, TunnelCipher.AssociatedData);
        }

        return plain;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_key is null)
                return;
            CryptographicOperations.ZeroMemory(_key);
            if (_pin.IsAllocated)
                _pin.Free();
            _key = null;
        }
    }
}

internal static class TunnelCipher
{
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 1;
    public const int MaxIterations = 2_000_000;
    public const int KeyBytes = 32;
    public const int SaltBytes = 16;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const string KdfName = "pbkdf2-sha256";
    public const int Version = 1;

    internal static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("tunnelman-session/1");

    public static bool IterationsInRange(int iterations) =>
        iterations is >= MinIterations and <= MaxIterations;
}
