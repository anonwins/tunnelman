using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tunnelman.Tunnels;

internal enum ExportReadStatus
{
    Ok,
    NotExport,
    NoTunnels,
    Unreadable,
}

internal readonly record struct ExportReadResult(
    ExportReadStatus Status,
    IReadOnlyList<TunnelRecord> Tunnels,
    string Error)
{
    public static ExportReadResult Ok(IReadOnlyList<TunnelRecord> tunnels) =>
        new(ExportReadStatus.Ok, tunnels, "");

    public static ExportReadResult Fail(ExportReadStatus status, string error) =>
        new(status, [], error);
}

internal sealed class TunnelExportDocument
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = TunnelTransfer.FormatName;

    [JsonPropertyName("version")]
    public int Version { get; set; } = TunnelTransfer.ExportVersion;

    [JsonPropertyName("exportedAt")]
    public DateTimeOffset ExportedAt { get; set; }

    [JsonPropertyName("tunnels")]
    public List<TunnelRecord> Tunnels { get; set; } = [];
}

internal sealed class TransferEntry
{
    public required TunnelRecord Source { get; init; }
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
    public const string ReservedPath = "That path is used by Tunnelman for the session. Choose another file.";
    public const string FileFilter = "Tunnelman export (*.tunnelman.json)|*.tunnelman.json|JSON (*.json)|*.json";

    public static void Write(string path, IEnumerable<TunnelRecord> records)
    {
        var document = new TunnelExportDocument
        {
            Format = FormatName,
            Version = ExportVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Tunnels = records.Select(record => record.Clone()).ToList(),
        };
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(document, TunnelStore.JsonOptions));
    }

    public static ExportReadResult Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExportReadResult.Fail(ExportReadStatus.Unreadable, CouldNotRead);
        }

        return ReadDocument(text);
    }

    private static ExportReadResult ReadDocument(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return ExportReadResult.Fail(ExportReadStatus.NotExport, NotExport);
        }

        if (root is not JsonObject obj)
            return ExportReadResult.Fail(ExportReadStatus.NotExport, NotExport);

        if (!TryGet(obj, "format", out var formatNode)
            || formatNode is not JsonValue formatValue
            || !formatValue.TryGetValue<string>(out var format)
            || !format.Equals(FormatName, StringComparison.OrdinalIgnoreCase))
            return ExportReadResult.Fail(ExportReadStatus.NotExport, NotExport);

        if (!TryReadInt(obj, "version", required: true, out var exportVersion) || exportVersion != ExportVersion)
            return ExportReadResult.Fail(ExportReadStatus.NotExport, NotExport);

        if (!TryGet(obj, "tunnels", out var tunnelsNode) || tunnelsNode is not JsonArray array)
            return ExportReadResult.Fail(ExportReadStatus.NotExport, NotExport);

        var list = new List<TunnelRecord>();
        foreach (var item in array)
        {
            if (item is not JsonObject)
                return ExportReadResult.Fail(ExportReadStatus.NotExport, NotExport);

            try
            {
                var record = item.Deserialize<TunnelRecord>(TunnelStore.JsonOptions);
                if (record is null)
                    return ExportReadResult.Fail(ExportReadStatus.NotExport, NotExport);
                list.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return ExportReadResult.Fail(ExportReadStatus.NotExport, NotExport);
            }
        }

        if (list.Count == 0)
            return ExportReadResult.Fail(ExportReadStatus.NoTunnels, NoTunnels);
        return ExportReadResult.Ok(list);
    }

    public static IReadOnlyList<TransferEntry> BuildExport(IEnumerable<TunnelRecord> snapshots) =>
        snapshots.Select(snapshot =>
        {
            var source = snapshot.Clone();
            return Describe(source.Clone(), source, remap: false, names: null, listens: null);
        }).ToList();

    public static IReadOnlyList<TransferEntry> PrepareImport(
        IEnumerable<TunnelRecord> existing,
        IEnumerable<TunnelRecord> incoming)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listens = new List<(string Host, int Port)>();
        foreach (var record in existing)
        {
            names.Add(record.Name);
            listens.Add((record.ListenAddress, record.ListenPort));
        }

        var rows = new List<TransferEntry>();
        foreach (var raw in incoming)
        {
            var source = raw.Clone();
            rows.Add(Describe(source.Clone(), source, remap: true, names, listens));
        }

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

    public static bool IsReservedSessionPath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var reserved in new[] { AppPaths.TunnelsFile, AppPaths.UiFile })
        {
            string reservedFull;
            try
            {
                reservedFull = Path.GetFullPath(reserved);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (full.Equals(reservedFull, StringComparison.OrdinalIgnoreCase)
                || full.Equals(reservedFull + ".bak", StringComparison.OrdinalIgnoreCase)
                || full.Equals(reservedFull + ".tmp", StringComparison.OrdinalIgnoreCase))
                return true;

            var directory = Path.GetDirectoryName(full);
            var reservedDirectory = Path.GetDirectoryName(reservedFull);
            if (directory is null || reservedDirectory is null
                || !directory.Equals(reservedDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(full);
            var reservedName = Path.GetFileName(reservedFull);
            if (name.StartsWith(reservedName + ".", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".prev", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    public static string CountLabel(string verb, int count) =>
        count == 1 ? $"{verb} 1 tunnel." : $"{verb} {count} tunnels.";

    public static string SelectedLabel(int count) =>
        count == 0 ? "None selected" : count == 1 ? "1 selected" : $"{count} selected";

    public static string PrimaryLabel(bool export, int count)
    {
        var action = export ? "Export" : "Import";
        if (count == 0)
            return $"{action} tunnels";
        if (count == 1)
            return $"{action} tunnel";
        return $"{action} {count} tunnels";
    }

    private static TransferEntry Describe(
        TunnelRecord record,
        TunnelRecord source,
        bool remap,
        HashSet<string>? names,
        List<(string Host, int Port)>? listens)
    {
        var displayName = string.IsNullOrWhiteSpace(record.Name) ? "(unnamed)" : record.Name.Trim();
        var listenText = record.ListenText;
        var remoteText = record.RemoteText;

        if (string.IsNullOrWhiteSpace(record.Name) || !record.TryValidateEndpoints(out _))
            return Invalid(record, source, displayName, listenText, remoteText);

        string? note = null;
        if (remap && names is not null)
        {
            var originalName = record.Name.Trim();
            record.Name = UniqueName(originalName, names);
            if (!record.Name.Equals(originalName, StringComparison.Ordinal))
                note = $"Renamed from {originalName}";
        }

        if (remap && listens is not null)
        {
            var originalKey = record.ListenKey;
            if (!ListenOccupancy.TryReserve(record, listens))
                return Invalid(record, source, record.Name, listenText, remoteText);

            if (!record.ListenKey.Equals(originalKey, StringComparison.OrdinalIgnoreCase))
            {
                listenText = record.ListenText;
                note = note is null
                    ? $"Listen was {originalKey}"
                    : $"{note} · Listen was {originalKey}";
            }
        }

        if (remap && names is not null)
            names.Add(record.Name);

        listenText = record.ListenText;
        remoteText = record.RemoteText;
        return new TransferEntry
        {
            Source = source,
            Record = record,
            DisplayName = remap ? record.Name : displayName,
            ListenText = listenText,
            RemoteText = remoteText,
            Note = note,
            CanSelect = true,
        };
    }

    private static TransferEntry Invalid(
        TunnelRecord record,
        TunnelRecord source,
        string displayName,
        string listenText,
        string remoteText) =>
        new()
        {
            Source = source,
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
