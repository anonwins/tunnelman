using Tunnelman;

namespace Tunnelman.Tests;

public sealed class NcatResolverTests
{
    [Fact]
    public void LooksLikeNmapNcat_requires_ncat_and_nmap()
    {
        Assert.True(NcatResolver.LooksLikeNmapNcat("Ncat: Version 7.95 ( https://nmap.org/ncat )"));
        Assert.False(NcatResolver.LooksLikeNmapNcat("ncat 1.0"));
        Assert.False(NcatResolver.LooksLikeNmapNcat(""));
    }

    [Fact]
    public void Resolve_prefers_configured_then_adjacent_then_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-ncat-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var configured = Path.Combine(dir, "configured", "ncat.exe");
        var adjacent = Path.Combine(dir, "app", "ncat.exe");
        var onPath = Path.Combine(dir, "path", "ncat.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(configured)!);
        Directory.CreateDirectory(Path.GetDirectoryName(adjacent)!);
        Directory.CreateDirectory(Path.GetDirectoryName(onPath)!);
        File.WriteAllText(configured, "x");
        File.WriteAllText(adjacent, "x");
        File.WriteAllText(onPath, "x");
        try
        {
            string Probe(string path) => "Ncat nmap";
            var first = NcatResolver.Resolve(new NcatResolveContext
            {
                ConfiguredPath = configured,
                AppDirectory = Path.GetDirectoryName(adjacent)!,
                PathEnvironment = Path.GetDirectoryName(onPath),
                WellKnownPaths = [],
                FileExists = File.Exists,
                VersionProbe = Probe,
            });
            Assert.Equal(configured, first.Path);

            var second = NcatResolver.Resolve(new NcatResolveContext
            {
                ConfiguredPath = null,
                AppDirectory = Path.GetDirectoryName(adjacent)!,
                PathEnvironment = Path.GetDirectoryName(onPath),
                WellKnownPaths = [],
                FileExists = File.Exists,
                VersionProbe = Probe,
            });
            Assert.Equal(adjacent, second.Path);

            var third = NcatResolver.Resolve(new NcatResolveContext
            {
                ConfiguredPath = null,
                AppDirectory = Path.Combine(dir, "empty-app"),
                PathEnvironment = Path.GetDirectoryName(onPath),
                WellKnownPaths = [],
                FileExists = File.Exists,
                VersionProbe = Probe,
            });
            Assert.Equal(onPath, third.Path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_skips_non_nmap_binaries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-ncat-skip-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var fake = Path.Combine(dir, "ncat.exe");
        File.WriteAllText(fake, "x");
        try
        {
            var result = NcatResolver.Resolve(new NcatResolveContext
            {
                ConfiguredPath = fake,
                AppDirectory = dir,
                PathEnvironment = "",
                WellKnownPaths = [],
                FileExists = File.Exists,
                VersionProbe = _ => "something else",
            });
            Assert.False(result.Found);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"?:\not|a|path")]
    [InlineData("file:////not a path")]
    public void Candidates_skips_invalid_configured_paths(string configured)
    {
        var result = Record.Exception(() => NcatResolver.Resolve(new NcatResolveContext
        {
            ConfiguredPath = configured,
            AppDirectory = Path.GetTempPath(),
            PathEnvironment = "",
            WellKnownPaths = [configured],
            FileExists = _ => true,
            VersionProbe = _ => "Ncat nmap",
        }));
        Assert.Null(result);
        var candidates = NcatResolver.Candidates(new NcatResolveContext
        {
            ConfiguredPath = configured,
            AppDirectory = Path.GetTempPath(),
            PathEnvironment = "",
            WellKnownPaths = [configured],
        }).ToList();
        Assert.DoesNotContain(configured, candidates);
    }
}
