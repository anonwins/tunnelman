namespace Tunnelman.Tunnels;

internal static class TunnelKindRegistry
{
    private static readonly Dictionary<string, ITunnelKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [TunnelKinds.NcatSocks] = new NcatSocksKind(),
    };

    public static IEnumerable<ITunnelKind> All => Kinds.Values;

    public static ITunnelKind? Find(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return null;
        return Kinds.TryGetValue(kind.Trim(), out var found) ? found : null;
    }

    public static string Canonicalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return TunnelKinds.NcatSocks;
        var trimmed = kind.Trim();
        return Find(trimmed)?.Kind ?? trimmed;
    }

    public static bool Is(string? kind, string expected) =>
        Find(kind)?.Kind == expected;
}
