namespace Tunnelman;

internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        contents ??= "";

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        string? backup = null;
        try
        {
            File.WriteAllText(tmp, contents);
            if (File.Exists(path))
            {
                backup = path + "." + Guid.NewGuid().ToString("n") + ".prev";
                File.Replace(tmp, path, backup, ignoreMetadataErrors: false);
                if (!IsReadable(path))
                    throw new IOException("The written file could not be read.");
                TryDelete(backup);
                backup = null;
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            if (TryRecover(path, tmp, backup))
                return;
            TryDelete(tmp);
            throw;
        }
    }

    internal static bool TryRecover(string path, string tmp, string? backup)
    {
        if (File.Exists(path))
            return false;

        if (File.Exists(tmp))
        {
            File.Move(tmp, path);
            return true;
        }

        if (backup is not null && File.Exists(backup))
        {
            try
            {
                File.Move(backup, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static bool IsReadable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
