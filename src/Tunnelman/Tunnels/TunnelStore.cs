using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tunnelman.Tunnels;

internal enum TunnelProbeKind
{
    Missing,
    Plain,
    Foreign,
    Unreadable,
}

internal readonly record struct TunnelProbe(TunnelProbeKind Kind);

internal enum TunnelLoadStatus
{
    Ok,
    Missing,
    Foreign,
    Unreadable,
}

internal readonly record struct TunnelLoadResult(
    TunnelLoadStatus Status,
    IReadOnlyList<TunnelRecord> Tunnels)
{
    public static TunnelLoadResult Ok(IReadOnlyList<TunnelRecord> tunnels) =>
        new(TunnelLoadStatus.Ok, tunnels);

    public static TunnelLoadResult Empty() =>
        new(TunnelLoadStatus.Missing, []);

    public static TunnelLoadResult Fail(TunnelLoadStatus status) =>
        new(status, []);
}

internal sealed class TunnelDocument
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("tunnels")]
    public List<TunnelRecord> Tunnels { get; set; } = [];
}

internal sealed class TunnelStore
{
    public const string CouldNotRead = "Could not read the session file.";
    public const string ForeignFile =
        "This session file is not a Tunnelman tunnel list. Move it aside, then start Tunnelman again.";

    internal static readonly JsonSerializerOptions JsonOptions = new()
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

    public TunnelProbe Probe()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return new TunnelProbe(TunnelProbeKind.Missing);
            try
            {
                var text = File.ReadAllText(_filePath);
                return TryReadDocument(text, out _)
                    ? new TunnelProbe(TunnelProbeKind.Plain)
                    : new TunnelProbe(TunnelProbeKind.Foreign);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TunnelProbe(TunnelProbeKind.Unreadable);
            }
        }
    }

    public TunnelLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return TunnelLoadResult.Empty();

            string text;
            try
            {
                text = File.ReadAllText(_filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return TunnelLoadResult.Fail(TunnelLoadStatus.Unreadable);
            }

            if (!TryReadDocument(text, out var tunnels))
                return TunnelLoadResult.Fail(TunnelLoadStatus.Foreign);
            return TunnelLoadResult.Ok(tunnels);
        }
    }

    public void Save(IEnumerable<TunnelRecord> tunnels)
    {
        lock (_gate)
        {
            var document = new TunnelDocument
            {
                Version = TunnelDocument.CurrentVersion,
                Tunnels = tunnels.Select(tunnel => tunnel.Clone()).ToList(),
            };
            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(document, JsonOptions));
            AppPaths.TryProtectWrittenFile(_filePath);
        }
    }

    private static bool TryReadDocument(string text, out List<TunnelRecord> tunnels)
    {
        tunnels = [];
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is not JsonObject obj)
            return false;
        if (HasMember(obj, "format") || HasMember(obj, "kdf") || HasMember(obj, "ciphertext") || HasMember(obj, "purpose"))
            return false;
        if (!TryReadInt(obj, "version", out var version) || version != TunnelDocument.CurrentVersion)
            return false;
        if (!TryGet(obj, "tunnels", out var tunnelsNode) || tunnelsNode is not JsonArray array)
            return false;

        var list = new List<TunnelRecord>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject)
                return false;
            try
            {
                var record = item.Deserialize<TunnelRecord>(JsonOptions);
                if (record is null)
                    return false;
                list.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return false;
            }
        }

        tunnels = list;
        return true;
    }

    private static bool HasMember(JsonObject obj, string name) =>
        TryGet(obj, name, out var node) && node is not null && node.GetValueKind() != JsonValueKind.Null;

    private static bool TryGet(JsonObject obj, string name, out JsonNode? node)
    {
        foreach (var pair in obj)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                node = pair.Value;
                return true;
            }
        }

        node = null;
        return false;
    }

    private static bool TryReadInt(JsonObject obj, string name, out int value)
    {
        value = 0;
        return TryGet(obj, name, out var node)
            && node is JsonValue json
            && json.TryGetValue(out value);
    }
}
