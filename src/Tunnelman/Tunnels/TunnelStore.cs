using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tunnelman.Tunnels;

internal sealed class TunnelStoreDocument
{
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("tunnels")]
    public List<TunnelRecord> Tunnels { get; set; } = [];
}

internal sealed class TunnelStore
{
    public const int CurrentVersion = 1;
    public const string CouldNotRead = "Could not read the session file.";

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

    public TunnelStoreLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return TunnelStoreLoadResult.Ok([]);

            string text;
            try
            {
                text = File.ReadAllText(_filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return TunnelStoreLoadResult.FailUnreadable(CouldNotRead);
            }

            TunnelStoreDocument parsed;
            try
            {
                var document = JsonSerializer.Deserialize<TunnelStoreDocument>(text, JsonOptions);
                if (document is null)
                    return FailCorrupt("The session file was empty.");
                parsed = document;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return FailCorrupt(ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(parsed.Format))
            {
                if (parsed.Format.Equals(TunnelTransfer.FormatName, StringComparison.OrdinalIgnoreCase))
                    return ReplaceExportWithEmpty();
                return FailCorrupt("This file is not a Tunnelman session.");
            }

            if (parsed.Version != CurrentVersion)
                return FailCorrupt($"Unsupported session version {parsed.Version}.");

            if (parsed.Tunnels is null || parsed.Tunnels.Any(t => t is null))
                return FailCorrupt("The session file was empty.");

            return TunnelStoreLoadResult.Ok(parsed.Tunnels.Select(Clone).ToList());
        }
    }

    public void Save(IEnumerable<TunnelRecord> tunnels)
    {
        lock (_gate)
        {
            WriteDocument(tunnels);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            WriteDocument([]);
        }
    }

    private void WriteDocument(IEnumerable<TunnelRecord> tunnels)
    {
        var document = new TunnelStoreDocument
        {
            Version = CurrentVersion,
            Tunnels = tunnels.Select(Clone).ToList(),
        };
        AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(document, JsonOptions));
        AppPaths.TryProtectWrittenFile(_filePath);
    }

    private TunnelStoreLoadResult ReplaceExportWithEmpty()
    {
        var bak = CopyBak();
        try
        {
            WriteDocument([]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return bak
                ? TunnelStoreLoadResult.FailCorrupt([])
                : TunnelStoreLoadResult.FailUnreadable(CouldNotRead);
        }

        return bak
            ? TunnelStoreLoadResult.FailCorrupt([])
            : new TunnelStoreLoadResult([], CouldNotRead, Corrupt: true, Unreadable: false, BakCopied: false);
    }

    private TunnelStoreLoadResult FailCorrupt(string message)
    {
        var bak = CopyBak();
        return new TunnelStoreLoadResult([], message, Corrupt: true, Unreadable: false, BakCopied: bak);
    }

    private bool CopyBak()
    {
        var bak = _filePath + ".bak";
        try
        {
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, bak, overwrite: true);
                return File.Exists(bak);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static TunnelRecord Clone(TunnelRecord record) => record.Clone();
}

internal readonly record struct TunnelStoreLoadResult(
    IReadOnlyList<TunnelRecord> Tunnels,
    string? Error,
    bool Corrupt,
    bool Unreadable = false,
    bool BakCopied = false)
{
    public static TunnelStoreLoadResult Ok(IReadOnlyList<TunnelRecord> tunnels) =>
        new(tunnels, null, false);

    public static TunnelStoreLoadResult FailUnreadable(string error) =>
        new([], error, Corrupt: false, Unreadable: true);

    public static TunnelStoreLoadResult FailCorrupt(IReadOnlyList<TunnelRecord> tunnels) =>
        new(tunnels, "Previous session file was unreadable and was copied to tunnels.json.bak.", true, false, true);
}
