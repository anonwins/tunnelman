using System.Text.Json.Nodes;

namespace Tunnelman.Tunnels;

internal sealed class NcatSocksOptions
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 22001;
    public string SocksHost { get; set; } = "127.0.0.1";
    public int SocksPort { get; set; } = 25560;
    public string RemoteHost { get; set; } = "";
    public int RemotePort { get; set; } = 22;

    public static NcatSocksOptions Defaults() => new();

    public static bool TryParse(JsonObject? options, out NcatSocksOptions parsed, out string error)
    {
        parsed = Defaults();
        error = "";
        if (options is null)
        {
            error = "Missing tunnel options.";
            return false;
        }

        parsed.ListenAddress = ReadString(options, "listenAddress", parsed.ListenAddress);
        parsed.SocksHost = ReadString(options, "socksHost", parsed.SocksHost);
        parsed.RemoteHost = ReadString(options, "remoteHost", parsed.RemoteHost);
        if (!TryReadPort(options, "listenPort", parsed.ListenPort, out var listenPort, out error))
            return false;
        parsed.ListenPort = listenPort;
        if (!TryReadPort(options, "socksPort", parsed.SocksPort, out var socksPort, out error))
            return false;
        parsed.SocksPort = socksPort;
        if (!TryReadPort(options, "remotePort", parsed.RemotePort, out var remotePort, out error))
            return false;
        parsed.RemotePort = remotePort;
        return true;
    }

    public JsonObject ToJson() => new()
    {
        ["listenAddress"] = ListenAddress,
        ["listenPort"] = ListenPort,
        ["socksHost"] = SocksHost,
        ["socksPort"] = SocksPort,
        ["remoteHost"] = RemoteHost,
        ["remotePort"] = RemotePort,
    };

    public string ListenText => $"{ListenAddress}:{ListenPort}";
    public string RemoteText => $"{RemoteHost}:{RemotePort}";
    public string ProxyText => $"SOCKS5 {SocksHost}:{SocksPort}";
    public bool HasProxy => !string.IsNullOrWhiteSpace(SocksHost) && SocksPort is >= 1 and <= 65535;
    public string EndpointText => $"{ListenText} → {RemoteText} via {SocksHost}:{SocksPort}";

    private static string ReadString(JsonObject options, string name, string fallback)
    {
        if (options[name] is JsonValue value && value.TryGetValue<string>(out var text))
            return text?.Trim() ?? fallback;
        return fallback;
    }

    private static bool TryReadPort(JsonObject options, string name, int fallback, out int port, out string error)
    {
        error = "";
        port = fallback;
        if (options[name] is null)
            return true;
        if (options[name] is JsonValue value && value.TryGetValue<int>(out var number))
        {
            port = number;
            return true;
        }

        error = $"Invalid {name}.";
        return false;
    }
}
