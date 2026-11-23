using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tunnelman.Tunnels;

internal sealed class TunnelRecord
{
    private static readonly Regex Hostname = new(
        @"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("listenAddress")]
    public string ListenAddress { get; set; } = "127.0.0.1";

    [JsonPropertyName("listenPort")]
    public int ListenPort { get; set; } = 22001;

    [JsonPropertyName("socksHost")]
    public string SocksHost { get; set; } = "";

    [JsonPropertyName("socksPort")]
    public int SocksPort { get; set; }

    [JsonPropertyName("remoteHost")]
    public string RemoteHost { get; set; } = "";

    [JsonPropertyName("remotePort")]
    public int RemotePort { get; set; } = 22;

    public string ListenKey => $"{ListenAddress.Trim()}:{ListenPort}";
    public string ListenText => $"{ListenAddress}:{ListenPort}";
    public string RemoteText => $"{RemoteHost}:{RemotePort}";
    public string ProxyText => $"SOCKS5 {SocksHost}:{SocksPort}";
    public bool HasProxy => !string.IsNullOrWhiteSpace(SocksHost) && SocksPort is >= 1 and <= 65535;

    public static TunnelRecord Defaults() => new();

    public TunnelRecord Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        ListenAddress = ListenAddress,
        ListenPort = ListenPort,
        SocksHost = SocksHost,
        SocksPort = SocksPort,
        RemoteHost = RemoteHost,
        RemotePort = RemotePort,
    };

    public const string SocksPairRequired = "Enter a SOCKS5 host and port, or leave both empty.";

    public bool TryValidateEndpoints(out string error)
    {
        if (!TryValidateHost(ListenAddress, out error))
            return false;
        if (!TryValidatePort(ListenPort, out error))
            return false;
        if (!TryValidateSocks(out error))
            return false;
        if (!TryValidateHost(RemoteHost, out error))
            return false;
        return TryValidatePort(RemotePort, out error);
    }

    private bool TryValidateSocks(out string error)
    {
        var hostBlank = string.IsNullOrWhiteSpace(SocksHost);
        var portUnset = SocksPort == 0;
        if (hostBlank && portUnset)
        {
            error = "";
            return true;
        }

        if (!hostBlank && !TryValidateHost(SocksHost, out error))
            return false;
        if (hostBlank || portUnset || !TryValidatePort(SocksPort, out _))
        {
            error = SocksPairRequired;
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryValidateHost(string? host, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Host is required.";
            return false;
        }

        var value = host.Trim();
        if (value.IndexOfAny(['&', '|', '<', '>', '^', '%', '"', '\'', '`']) >= 0)
        {
            error = "Host contains characters that are not allowed.";
            return false;
        }

        if (IsIPv4(value) || Hostname.IsMatch(value))
            return true;

        error = "Host must be an IPv4 address or a hostname.";
        return false;
    }

    public static bool TryValidatePort(int port, out string error)
    {
        if (port is >= 1 and <= 65535)
        {
            error = "";
            return true;
        }

        error = "Port must be between 1 and 65535.";
        return false;
    }

    public static bool IsIPv4(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 4)
            return false;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3)
                return false;
            if (!int.TryParse(part, out var octet) || octet is < 0 or > 255)
                return false;
            if (part.Length > 1 && part[0] == '0')
                return false;
        }

        return true;
    }
}
