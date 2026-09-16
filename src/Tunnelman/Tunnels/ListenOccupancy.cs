using System.Text.Json.Nodes;

namespace Tunnelman.Tunnels;

internal static class ListenOccupancy
{
    public static string NormalizeHost(string host)
    {
        var trimmed = host.Trim();
        return trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : trimmed;
    }

    public static bool Overlaps(string hostA, int portA, string hostB, int portB)
    {
        if (portA != portB)
            return false;
        if (IsUnspecified(hostA) || IsUnspecified(hostB))
            return true;
        return NormalizeHost(hostA).Equals(NormalizeHost(hostB), StringComparison.OrdinalIgnoreCase);
    }

    public static bool ConflictsWithAny(string host, int port, IEnumerable<(string Host, int Port)> occupied)
    {
        foreach (var other in occupied)
        {
            if (Overlaps(host, port, other.Host, other.Port))
                return true;
        }

        return false;
    }

    public static bool TryReserve(NcatSocksOptions hop, TunnelRecord record, List<(string Host, int Port)> occupied)
    {
        var address = hop.ListenAddress.Trim();
        var port = hop.ListenPort;
        if (!ConflictsWithAny(address, port, occupied))
        {
            occupied.Add((address, port));
            return true;
        }

        while (port < 65535)
        {
            port++;
            if (ConflictsWithAny(address, port, occupied))
                continue;
            hop.ListenPort = port;
            var clone = record.Options is null ? new JsonObject() : (JsonObject)record.Options.DeepClone();
            clone["listenPort"] = port;
            record.Options = clone;
            occupied.Add((address, port));
            return true;
        }

        return false;
    }

    private static bool IsUnspecified(string host) =>
        host.Trim().Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase);
}
