using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tunnelman.Tunnels;

internal enum TunnelProbeKind
{
    Missing,
    Envelope,
    Foreign,
    Unreadable,
}

internal readonly record struct TunnelProbe(TunnelProbeKind Kind);

internal enum TunnelOpenStatus
{
    Ok,
    WrongPassphrase,
    Unreadable,
    Foreign,
}

internal readonly record struct TunnelOpenResult(
    TunnelOpenStatus Status,
    IReadOnlyList<TunnelRecord> Tunnels,
    SessionKey? Key)
{
    public static TunnelOpenResult Ok(IReadOnlyList<TunnelRecord> tunnels, SessionKey key) =>
        new(TunnelOpenStatus.Ok, tunnels, key);

    public static TunnelOpenResult Fail(TunnelOpenStatus status) =>
        new(status, [], null);
}

internal sealed class TunnelPayload
{
    [JsonPropertyName("tunnels")]
    public List<TunnelRecord> Tunnels { get; set; } = [];
}

internal sealed class TunnelEnvelope
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = TunnelCipher.Version;

    [JsonPropertyName("kdf")]
    public string? Kdf { get; set; } = TunnelCipher.KdfName;

    [JsonPropertyName("iterations")]
    public int Iterations { get; set; }

    [JsonPropertyName("salt")]
    public string? Salt { get; set; }

    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    [JsonPropertyName("ciphertext")]
    public string? Ciphertext { get; set; }
}

internal sealed class TunnelStore
{
    public const string CouldNotRead = "Could not read the session file.";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private byte[]? _salt;
    private int _iterations;

    public TunnelStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.TunnelsFile;
    }

    public string FilePath => _filePath;

    internal bool TryCopyMaterial(out byte[] salt, out int iterations)
    {
        lock (_gate)
        {
            if (_salt is null || !TunnelCipher.IterationsInRange(_iterations))
            {
                salt = [];
                iterations = 0;
                return false;
            }

            salt = _salt.ToArray();
            iterations = _iterations;
            return true;
        }
    }

    public TunnelProbe Probe()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return new TunnelProbe(TunnelProbeKind.Missing);
            try
            {
                var text = File.ReadAllText(_filePath);
                return Classify(text) == TunnelProbeKind.Envelope
                    ? new TunnelProbe(TunnelProbeKind.Envelope)
                    : new TunnelProbe(TunnelProbeKind.Foreign);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TunnelProbe(TunnelProbeKind.Unreadable);
            }
        }
    }

    public TunnelOpenResult Open(string passphrase)
    {
        lock (_gate)
        {
            string text;
            try
            {
                if (!File.Exists(_filePath))
                    return TunnelOpenResult.Fail(TunnelOpenStatus.Foreign);
                text = File.ReadAllText(_filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return TunnelOpenResult.Fail(TunnelOpenStatus.Unreadable);
            }

            if (!TryReadEnvelope(text, out var envelope, out var salt, out var nonce, out var cipherAndTag))
                return TunnelOpenResult.Fail(TunnelOpenStatus.Foreign);

            SessionKey? key = null;
            try
            {
                key = SessionKey.Derive(passphrase, salt, envelope.Iterations);
                var plain = key.Decrypt(nonce, cipherAndTag);
                TunnelPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<TunnelPayload>(plain, JsonOptions);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }

                if (payload?.Tunnels is null || payload.Tunnels.Any(t => t is null))
                {
                    key.Dispose();
                    return TunnelOpenResult.Fail(TunnelOpenStatus.Unreadable);
                }

                _salt = salt.ToArray();
                _iterations = envelope.Iterations;
                return TunnelOpenResult.Ok(payload.Tunnels.Select(t => t.Clone()).ToList(), key);
            }
            catch (AuthenticationTagMismatchException)
            {
                key?.Dispose();
                return TunnelOpenResult.Fail(TunnelOpenStatus.WrongPassphrase);
            }
            catch (CryptographicException)
            {
                key?.Dispose();
                return TunnelOpenResult.Fail(TunnelOpenStatus.Unreadable);
            }
            catch (JsonException)
            {
                key?.Dispose();
                return TunnelOpenResult.Fail(TunnelOpenStatus.Unreadable);
            }
        }
    }

    public SessionKey? Create(string passphrase, int iterations, IReadOnlyList<TunnelRecord> tunnels)
    {
        lock (_gate)
        {
            if (!TunnelCipher.IterationsInRange(iterations))
                return null;
            var salt = RandomNumberGenerator.GetBytes(TunnelCipher.SaltBytes);
            var key = SessionKey.Derive(passphrase, salt, iterations);
            try
            {
                _salt = salt;
                _iterations = iterations;
                Write(tunnels, key);
                return key;
            }
            catch
            {
                _salt = null;
                _iterations = 0;
                key.Dispose();
                throw;
            }
        }
    }

    public void Save(IEnumerable<TunnelRecord> tunnels, SessionKey key)
    {
        lock (_gate)
        {
            if (_salt is null || !TunnelCipher.IterationsInRange(_iterations))
                throw new InvalidOperationException("The session key is not ready.");
            Write(tunnels, key);
        }
    }

    private void Write(IEnumerable<TunnelRecord> tunnels, SessionKey key)
    {
        var payload = new TunnelPayload
        {
            Tunnels = tunnels.Select(t => t.Clone()).ToList(),
        };
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        byte[] nonce;
        byte[] cipher;
        try
        {
            cipher = key.Encrypt(plain, out nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var envelope = new TunnelEnvelope
        {
            Version = TunnelCipher.Version,
            Kdf = TunnelCipher.KdfName,
            Iterations = _iterations,
            Salt = Convert.ToBase64String(_salt!),
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(cipher),
        };
        AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(envelope, JsonOptions));
        AppPaths.TryProtectWrittenFile(_filePath);
    }

    private static TunnelProbeKind Classify(string text)
    {
        try
        {
            return TryReadEnvelope(text, out _, out _, out _, out _)
                ? TunnelProbeKind.Envelope
                : TunnelProbeKind.Foreign;
        }
        catch (JsonException)
        {
            return TunnelProbeKind.Foreign;
        }
        catch (FormatException)
        {
            return TunnelProbeKind.Foreign;
        }
    }

    private static bool TryReadEnvelope(
        string text,
        out TunnelEnvelope envelope,
        out byte[] salt,
        out byte[] nonce,
        out byte[] cipherAndTag)
    {
        envelope = new TunnelEnvelope();
        salt = [];
        nonce = [];
        cipherAndTag = [];
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is JsonObject obj
            && obj["purpose"] is JsonNode purpose
            && purpose.GetValueKind() != JsonValueKind.Null)
            return false;

        var parsed = JsonSerializer.Deserialize<TunnelEnvelope>(text, JsonOptions);
        if (parsed is null)
            return false;
        if (parsed.Version != TunnelCipher.Version)
            return false;
        if (!string.Equals(parsed.Kdf, TunnelCipher.KdfName, StringComparison.Ordinal))
            return false;
        if (!TunnelCipher.IterationsInRange(parsed.Iterations))
            return false;
        if (string.IsNullOrWhiteSpace(parsed.Salt)
            || string.IsNullOrWhiteSpace(parsed.Nonce)
            || string.IsNullOrWhiteSpace(parsed.Ciphertext))
            return false;

        salt = Convert.FromBase64String(parsed.Salt);
        nonce = Convert.FromBase64String(parsed.Nonce);
        cipherAndTag = Convert.FromBase64String(parsed.Ciphertext);
        if (salt.Length != TunnelCipher.SaltBytes
            || nonce.Length != TunnelCipher.NonceBytes
            || cipherAndTag.Length < TunnelCipher.TagBytes)
            return false;

        envelope = parsed;
        return true;
    }
}
