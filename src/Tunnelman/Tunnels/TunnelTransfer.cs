using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tunnelman.Tunnels;

internal sealed class TunnelExportDocument
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = TunnelTransfer.FormatName;

    [JsonPropertyName("version")]
    public int Version { get; set; } = TunnelTransfer.ExportVersion;

    [JsonPropertyName("exportedAt")]
    public DateTimeOffset ExportedAt { get; set; }

    [JsonPropertyName("tunnels")]
    public List<TunnelExportEntry> Tunnels { get; set; } = [];
}

internal sealed class TunnelExportEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("options")]
    public JsonObject? Options { get; set; }
}

internal sealed class TransferEntry
{
    public required TunnelRecord Record { get; init; }
    public required string DisplayName { get; init; }
    public string ListenText { get; init; } = "";
    public string RemoteText { get; init; } = "";
    public string? Note { get; init; }
    public bool CanSelect { get; init; }
    public bool ShowEndpoints => ListenText.Length > 0 || RemoteText.Length > 0;
    public bool HasNote => !string.IsNullOrEmpty(Note);
}

internal static class TunnelTransfer
{
    public const string FormatName = "tunnelman-export";
    public const int ExportVersion = 1;
    public const string NotExport = "This file isn't a Tunnelman export.";
    public const string NoTunnels = "This file has no tunnels.";
    public const string CouldNotRead = "Could not read this file.";
    public const string CouldNotWrite = "Could not write the file.";
    public const string FileFilter = "Tunnelman export (*.tunnelman.json)|*.tunnelman.json|JSON (*.json)|*.json";

    private static readonly string[] SecretKeys =
    [
        "token", "password", "secret", "passphrase", "credential", "apiKey", "privateKey",
    ];

    public static void Write(string path, IEnumerable<TunnelRecord> records)
    {
        var document = new TunnelExportDocument
        {
            Format = FormatName,
            Version = ExportVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Tunnels = records.Select(ToExportEntry).ToList(),
        };
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(document, TunnelStore.JsonOptions));
    }

    public static bool TryRead(string path, out IReadOnlyList<TunnelRecord> records, out string error)
    {
        records = [];
        error = "";
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = CouldNotRead;
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            error = NotExport;
            return false;
        }

        if (root is not JsonObject obj)
        {
            error = NotExport;
            return false;
        }

        var hasFormat = TryGet(obj, "format", out var formatNode);
        if (hasFormat)
        {
            if (formatNode is not JsonValue formatValue
                || !formatValue.TryGetValue<string>(out var format)
                || !format.Equals(FormatName, StringComparison.OrdinalIgnoreCase))
            {
                error = NotExport;
                return false;
            }

            if (!TryReadInt(obj, "version", required: true, out var exportVersion) || exportVersion != ExportVersion)
            {
                error = NotExport;
                return false;
            }
        }
        else if (TryGet(obj, "version", out _)
            && (!TryReadInt(obj, "version", required: true, out var sessionVersion)
                || sessionVersion != TunnelStore.CurrentVersion))
        {
            error = NotExport;
            return false;
        }

        if (!TryGet(obj, "tunnels", out var tunnelsNode) || tunnelsNode is not JsonArray array)
        {
            error = NotExport;
            return false;
        }

        var list = new List<TunnelRecord>();
        foreach (var item in array)
        {
            if (item is not JsonObject)
            {
                error = NotExport;
                return false;
            }

            try
            {
                var record = item.Deserialize<TunnelRecord>(TunnelStore.JsonOptions);
                if (record is null)
                {
                    error = NotExport;
                    return false;
                }

                list.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                error = NotExport;
                return false;
            }
        }

        if (list.Count == 0)
        {
            error = NoTunnels;
            return false;
        }

        records = list;
        return true;
    }

    public static IReadOnlyList<TransferEntry> BuildExport(IEnumerable<TunnelRecord> snapshots) =>
        snapshots.Select(snapshot => Describe(snapshot.Clone(), remap: false, names: null, listens: null)).ToList();

    public static IReadOnlyList<TransferEntry> PrepareImport(
        IEnumerable<TunnelRecord> existing,
        IEnumerable<TunnelRecord> incoming)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in existing)
        {
            names.Add(record.Name);
            if (record.Kind == TunnelKinds.NcatSocks
                && NcatSocksOptions.TryParse(record.Options, out var options, out _))
                listens.Add(options.ListenKey);
        }

        var rows = new List<TransferEntry>();
        foreach (var raw in incoming)
            rows.Add(Describe(raw.Clone(), remap: true, names, listens));
        return rows;
    }

    public static string NormalizeExportPath(string path)
    {
        if (path.EndsWith(".tunnelman.json.json", StringComparison.OrdinalIgnoreCase))
            path = path[..^5];
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            path += ".tunnelman.json";
        return path;
    }

    public static string CountLabel(string verb, int count) =>
        count == 1 ? $"{verb} 1 tunnel." : $"{verb} {count} tunnels.";

    public static string SelectedLabel(int count) =>
        count == 0 ? "None selected" : count == 1 ? "1 selected" : $"{count} selected";

    public static string PrimaryLabel(bool export, int count)
    {
        var action = export ? "Export" : "Import";
        var noun = export ? "tunnels" : "tunnels";
        if (count == 0)
            return $"{action} {noun}";
        if (count == 1)
            return $"{action} tunnel";
        return $"{action} {count} tunnels";
    }

    private static TransferEntry Describe(
        TunnelRecord record,
        bool remap,
        HashSet<string>? names,
        HashSet<string>? listens)
    {
        record.Enabled = false;
        if (string.IsNullOrWhiteSpace(record.Kind))
            record.Kind = "";

        var displayName = string.IsNullOrWhiteSpace(record.Name) ? "(unnamed)" : record.Name.Trim();
        var listenText = "";
        var remoteText = "";
        if (record.Kind == TunnelKinds.NcatSocks
            && NcatSocksOptions.TryParse(record.Options, out var parsed, out _))
        {
            listenText = parsed.ListenText;
            remoteText = parsed.RemoteText;
        }

        if (string.IsNullOrWhiteSpace(record.Name))
            return Invalid(record, "(unnamed)", listenText, remoteText);

        var kind = TunnelKindRegistry.Find(record.Kind);
        if (kind is not null)
        {
            if (record.Options is null || !kind.TryValidate(record.Options, out _))
                return Invalid(record, displayName, listenText, remoteText);
        }

        string? note = null;
        if (remap && names is not null)
        {
            var originalName = record.Name.Trim();
            record.Name = UniqueName(originalName, names);
            if (!record.Name.Equals(originalName, StringComparison.Ordinal))
                note = $"Renamed from {originalName}";
        }

        if (remap
            && listens is not null
            && record.Kind == TunnelKinds.NcatSocks
            && record.Options is not null
            && NcatSocksOptions.TryParse(record.Options, out var hop, out _))
        {
            var originalKey = hop.ListenKey;
            if (!TryReserveListen(hop, record, listens, out var reserved))
                return Invalid(record, record.Name, listenText, remoteText);

            if (!reserved.Equals(originalKey, StringComparison.OrdinalIgnoreCase))
            {
                listenText = hop.ListenText;
                note = note is null
                    ? $"Listen was {originalKey}"
                    : $"{note} · Listen was {originalKey}";
            }
        }

        if (remap && names is not null)
            names.Add(record.Name);

        if (record.Kind == TunnelKinds.NcatSocks
            && NcatSocksOptions.TryParse(record.Options, out var finalHop, out _))
        {
            listenText = finalHop.ListenText;
            remoteText = finalHop.RemoteText;
        }

        return new TransferEntry
        {
            Record = record,
            DisplayName = remap ? record.Name : displayName,
            ListenText = listenText,
            RemoteText = remoteText,
            Note = note,
            CanSelect = true,
        };
    }

    private static TransferEntry Invalid(TunnelRecord record, string displayName, string listenText, string remoteText) =>
        new()
        {
            Record = record,
            DisplayName = displayName,
            ListenText = listenText,
            RemoteText = remoteText,
            Note = "This entry is invalid.",
            CanSelect = false,
        };

    private static string UniqueName(string trimmed, HashSet<string> names)
    {
        if (!names.Contains(trimmed))
            return trimmed;
        var n = 2;
        while (true)
        {
            var next = $"{trimmed} ({n})";
            if (!names.Contains(next))
                return next;
            n++;
        }
    }

    private static bool TryReserveListen(
        NcatSocksOptions hop,
        TunnelRecord record,
        HashSet<string> listens,
        out string key)
    {
        key = hop.ListenKey;
        var port = hop.ListenPort;
        var address = hop.ListenAddress.Trim();
        if (!listens.Contains(key))
        {
            listens.Add(key);
            return true;
        }

        while (port < 65535)
        {
            port++;
            key = $"{address}:{port}";
            if (listens.Contains(key))
                continue;
            hop.ListenPort = port;
            var clone = record.Options is null ? new JsonObject() : (JsonObject)record.Options.DeepClone();
            clone["listenPort"] = port;
            record.Options = clone;
            listens.Add(key);
            return true;
        }

        return false;
    }

    private static TunnelExportEntry ToExportEntry(TunnelRecord record) =>
        new()
        {
            Id = record.Id,
            Name = record.Name,
            Kind = record.Kind,
            Options = StripSecrets(record.Options),
        };

    private static JsonObject? StripSecrets(JsonObject? options)
    {
        if (options is null)
            return null;
        var clone = (JsonObject)options.DeepClone();
        foreach (var key in clone.Select(kv => kv.Key).ToList())
        {
            if (SecretKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                clone.Remove(key);
        }

        return clone;
    }

    private static bool TryGet(JsonObject obj, string name, out JsonNode? node)
    {
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                node = kv.Value;
                return true;
            }
        }

        node = null;
        return false;
    }

    private static bool TryReadInt(JsonObject obj, string name, bool required, out int value)
    {
        value = 0;
        if (!TryGet(obj, name, out var node))
            return !required;
        return node is JsonValue json && json.TryGetValue(out value);
    }
}
