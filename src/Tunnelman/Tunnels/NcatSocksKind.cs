using System.Text.Json.Nodes;

namespace Tunnelman.Tunnels;

internal sealed class NcatSocksKind : ITunnelKind
{
    public string Kind => TunnelKinds.NcatSocks;

    public string DisplayName => "ncat SOCKS5 hop";

    public bool TryValidate(JsonObject options, out string error)
    {
        if (!NcatSocksOptions.TryParse(options, out var parsed, out error))
            return false;
        return NcatCommandBuilder.TryValidate(parsed, out error);
    }

    public ITunnelRuntime CreateRuntime() => new NcatSocksRuntime();
}
