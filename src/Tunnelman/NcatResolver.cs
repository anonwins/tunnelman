using System.Diagnostics;
using System.Text;

namespace Tunnelman;

internal readonly record struct NcatResolveResult(string? Path, string? Error)
{
    public bool Found => !string.IsNullOrWhiteSpace(Path);
}

internal sealed class NcatResolveContext
{
    public string? ConfiguredPath { get; init; }
    public string AppDirectory { get; init; } = AppContext.BaseDirectory;
    public string? PathEnvironment { get; init; } = Environment.GetEnvironmentVariable("PATH");
    public IReadOnlyList<string> WellKnownPaths { get; init; } = DefaultWellKnownPaths();
    public Func<string, bool> FileExists { get; init; } = File.Exists;
    public Func<string, string?>? VersionProbe { get; init; }

    public static IReadOnlyList<string> DefaultWellKnownPaths()
    {
        var paths = new List<string>();
        Add(paths, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Nmap", "ncat.exe");
        Add(paths, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nmap", "ncat.exe");
        return paths;
    }

    private static void Add(List<string> paths, params string[] parts)
    {
        if (parts.Any(string.IsNullOrWhiteSpace))
            return;
        paths.Add(Path.Combine(parts));
    }
}

internal static class NcatResolver
{
    public static NcatResolveResult Resolve() =>
        Resolve(new NcatResolveContext
        {
            ConfiguredPath = UiSettings.Current.NcatPath,
            VersionProbe = TryReadVersion,
        });

    public static NcatResolveResult Resolve(NcatResolveContext context)
    {
        foreach (var candidate in Candidates(context))
        {
            if (!context.FileExists(candidate))
                continue;
            if (!LooksLikeNmapNcat(candidate, context))
                continue;
            return new NcatResolveResult(candidate, null);
        }

        return new NcatResolveResult(null, "Nmap ncat was not found.");
    }

    public static bool LooksLikeNmapNcat(string versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
            return false;
        return versionText.Contains("Ncat", StringComparison.OrdinalIgnoreCase)
               && versionText.Contains("nmap", StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<string> Candidates(NcatResolveContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ConfiguredPath))
            yield return Path.GetFullPath(context.ConfiguredPath);

        var adjacent = Path.Combine(context.AppDirectory, "ncat.exe");
        yield return Path.GetFullPath(adjacent);

        if (!string.IsNullOrWhiteSpace(context.PathEnvironment))
        {
            foreach (var dir in context.PathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string combined;
                try
                {
                    combined = Path.GetFullPath(Path.Combine(dir.Trim(), "ncat.exe"));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                yield return combined;
            }
        }

        foreach (var known in context.WellKnownPaths)
        {
            if (string.IsNullOrWhiteSpace(known))
                continue;
            string full;
            try
            {
                full = Path.GetFullPath(known);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            yield return full;
        }
    }

    private static bool LooksLikeNmapNcat(string path, NcatResolveContext context)
    {
        if (context.VersionProbe is null)
            return true;
        var text = context.VersionProbe(path);
        return LooksLikeNmapNcat(text ?? "");
    }

    internal static string? TryReadVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.SystemDirectory,
            };
            psi.ArgumentList.Add("--version");
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return null;
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            if (!process.WaitForExit(4000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return null;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }
}
