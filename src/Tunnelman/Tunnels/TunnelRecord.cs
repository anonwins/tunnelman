using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tunnelman.Tunnels;

internal sealed class TunnelRecord
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = TunnelKinds.NcatSocks;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("options")]
    public JsonObject? Options { get; set; }

    public TunnelRecord Clone() => new()
    {
        Id = Id,
        Name = Name,
        Kind = Kind,
        Enabled = Enabled,
        Options = Options is null ? null : (JsonObject)Options.DeepClone(),
    };
}
