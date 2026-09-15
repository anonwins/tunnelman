using System.Text.Json.Nodes;

namespace Tunnelman.Tunnels;

internal interface ITunnelKind
{
    string Kind { get; }
    string DisplayName { get; }
    bool TryValidate(JsonObject options, out string error);
    ITunnelRuntime CreateRuntime();
}
