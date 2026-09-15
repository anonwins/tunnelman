using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Tunnelman.Tunnels;

internal static class NcatCommandBuilder
{
    private static readonly Regex Hostname = new(
        @"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

    public static bool TryValidate(NcatSocksOptions options, out string error)
    {
        if (!TryValidateHost(options.ListenAddress, out error))
            return false;
        if (!TryValidatePort(options.ListenPort, out error))
            return false;
        if (!TryValidateHost(options.SocksHost, out error))
            return false;
        if (!TryValidatePort(options.SocksPort, out error))
            return false;
        if (!TryValidateHost(options.RemoteHost, out error))
            return false;
        return TryValidatePort(options.RemotePort, out error);
    }

    public static IReadOnlyList<string> BuildOuterArguments(string ncatPath, NcatSocksOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ncatPath);
        if (!TryValidate(options, out var error))
            throw new ArgumentException(error, nameof(options));

        return
        [
            "-v",
            "-l",
            options.ListenAddress.Trim(),
            options.ListenPort.ToString(),
            "--keep-open",
            "--exec",
            BuildExecCommand(ncatPath, options),
        ];
    }

    public static string BuildExecCommand(string ncatPath, NcatSocksOptions options)
    {
        var exe = QuoteForExec(ncatPath);
        var proxy = $"{options.SocksHost.Trim()}:{options.SocksPort}";
        return $"{exe} -v --proxy {proxy} --proxy-type socks5 {options.RemoteHost.Trim()} {options.RemotePort}";
    }

    public static ProcessStartInfo CreateStartInfo(string ncatPath, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ncatPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ncatPath) ?? Environment.SystemDirectory,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    internal static string QuoteForExec(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.IndexOfAny([' ', '\t']) >= 0)
            return "\"" + full + "\"";
        return full;
    }

    private static bool IsIPv4(string value)
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
