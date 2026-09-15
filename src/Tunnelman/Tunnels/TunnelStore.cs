using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tunnelman.Tunnels;

internal sealed class TunnelStoreDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("tunnels")]
    public List<TunnelRecord> Tunnels { get; set; } = [];
}

internal sealed class TunnelStore
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    public TunnelStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.TunnelsFile;
    }

    public string FilePath => _filePath;

    public TunnelStoreLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return new TunnelStoreLoadResult([], null, false);

            try
            {
                var parsed = JsonSerializer.Deserialize<TunnelStoreDocument>(File.ReadAllText(_filePath), JsonOptions);
                if (parsed is null)
                    return FailCorrupt("The session file was empty.");
                if (parsed.Version != CurrentVersion)
                    return FailCorrupt($"Unsupported session version {parsed.Version}.");
                return new TunnelStoreLoadResult(parsed.Tunnels.Select(Clone).ToList(), null, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return FailCorrupt(ex.Message);
            }
        }
    }

    public void Save(IEnumerable<TunnelRecord> tunnels)
    {
        lock (_gate)
        {
            var document = new TunnelStoreDocument
            {
                Version = CurrentVersion,
                Tunnels = tunnels.Select(Clone).ToList(),
            };
            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(document, JsonOptions));
            AppPaths.TryProtectWrittenFile(_filePath);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Save([]);
        }
    }

    private TunnelStoreLoadResult FailCorrupt(string message)
    {
        var bak = _filePath + ".bak";
        try
        {
            if (File.Exists(_filePath))
                File.Copy(_filePath, bak, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // keep going
        }

        return new TunnelStoreLoadResult([], message, true);
    }

    private static TunnelRecord Clone(TunnelRecord record) => record.Clone();
}

internal readonly record struct TunnelStoreLoadResult(
    IReadOnlyList<TunnelRecord> Tunnels,
    string? Error,
    bool Corrupt);
