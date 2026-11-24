using System.Security.Cryptography;
using System.Text;

namespace Tunnelman;

internal static class ProfileDirectoryOption
{
    public const string Flag = "--profile-dir";
    public const string MissingFolder = "Missing folder after --profile-dir.";
    public const string CouldNotUse = "Could not use that profile folder.";

    public static bool TryParse(IReadOnlyList<string> args, string baseDirectory, out string? directory, out string? error)
    {
        directory = null;
        error = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(Flag + "=", StringComparison.Ordinal))
            {
                if (!TryAccept(arg[(Flag.Length + 1)..], baseDirectory, out var full, out error))
                {
                    directory = null;
                    return false;
                }

                directory = full;
                continue;
            }

            if (arg.Equals(Flag, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count || !TryAccept(args[++i], baseDirectory, out var full, out error))
                {
                    directory = null;
                    error ??= MissingFolder;
                    return false;
                }

                directory = full;
                continue;
            }

            directory = null;
            error = arg.StartsWith('-')
                ? $"Unknown option: {arg}."
                : $"Unexpected argument: {arg}.";
            return false;
        }

        return true;
    }

    public static string InstanceSuffix(string? chosenFullPath)
    {
        if (chosenFullPath is null || SameDirectory(chosenFullPath, AppPaths.DefaultDirectory))
            return "";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(chosenFullPath).ToUpperInvariant()));
        return "." + Convert.ToHexString(hash)[..16];
    }

    private static bool TryAccept(string value, string baseDirectory, out string full, out string? error)
    {
        full = "";
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = MissingFolder;
            return false;
        }

        try
        {
            full = Path.GetFullPath(value, baseDirectory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = CouldNotUse;
            return false;
        }
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path);
}
