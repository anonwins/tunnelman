using Tunnelman;

namespace Tunnelman.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void TryProtectDirectory_protects_a_temp_dir_not_appdata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-acl-" + Guid.NewGuid().ToString("n"));
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            Assert.True(AppPaths.TryProtectDirectory(dir, file));
            Assert.True(Directory.Exists(dir));
            File.WriteAllText(file, "{}");
            Assert.True(AppPaths.TryProtectDirectory(dir, file));
            Assert.True(AppPaths.TryProtectWrittenFile(file));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
