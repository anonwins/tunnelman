namespace Tunnelman.Tunnels;

internal static class TunnelKindRegistry
{
    private static readonly Dictionary<string, ITunnelKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [TunnelKinds.NcatSocks] = new NcatSocksKind(),
    };

    public static ITunnelKind? Find(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return null;
        return Kinds.TryGetValue(kind, out var found) ? found : null;
    }
}
