using System.Diagnostics;
using System.Text;

namespace Tunnelman;

internal static class NcatInstaller
{
    public static bool TryFindWinget(out string path)
    {
        path = "";
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(local))
        {
            path = local;
            return true;
        }

        var env = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(env))
            return false;
        foreach (var dir in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "winget.exe");
            if (!File.Exists(candidate))
                continue;
            path = candidate;
            return true;
        }

        return false;
    }

    public static async Task<(bool Ok, string Output)> InstallNmapAsync(CancellationToken cancellationToken)
    {
        if (!TryFindWinget(out var winget))
            return (false, "winget was not found.");

        var psi = new ProcessStartInfo
        {
            FileName = winget,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("--id");
        psi.ArgumentList.Add("Insecure.Nmap");
        psi.ArgumentList.Add("--exact");
        psi.ArgumentList.Add("--accept-package-agreements");
        psi.ArgumentList.Add("--accept-source-agreements");

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
            return (false, "Could not start winget.");

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)).Trim();
        return (process.ExitCode == 0, output);
    }

    public static void OpenNmapDownloadPage()
    {
        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = "https://nmap.org/download.html",
            UseShellExecute = true,
        });
    }
}
