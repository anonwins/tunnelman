using System.Text.RegularExpressions;
using Tunnelman;

namespace Tunnelman.Tests;

public sealed class ProfileDirectoryOptionTests
{
    private const string Base = @"C:\work";

    [Fact]
    public void No_flag_uses_the_default()
    {
        Assert.True(ProfileDirectoryOption.TryParse([], Base, out var directory, out var error));
        Assert.Null(directory);
        Assert.Null(error);
        Assert.Equal("", ProfileDirectoryOption.InstanceSuffix(null));
    }

    [Fact]
    public void Spaced_flag_resolves_a_relative_path()
    {
        Assert.True(ProfileDirectoryOption.TryParse(["--profile-dir", "sub"], Base, out var directory, out var error));
        Assert.Null(error);
        Assert.Equal(@"C:\work\sub", directory);
    }

    [Fact]
    public void Equals_flag_resolves_a_relative_path()
    {
        Assert.True(ProfileDirectoryOption.TryParse(["--profile-dir=sub"], Base, out var directory, out var error));
        Assert.Null(error);
        Assert.Equal(@"C:\work\sub", directory);
    }

    [Fact]
    public void Last_flag_wins()
    {
        Assert.True(ProfileDirectoryOption.TryParse(
            ["--profile-dir", @"C:\A", "--profile-dir", @"C:\B"],
            Base,
            out var directory,
            out var error));
        Assert.Null(error);
        Assert.Equal(@"C:\B", directory);
    }

    [Theory]
    [InlineData("--profile-dir")]
    [InlineData("--profile-dir=")]
    [InlineData("--profile-dir=   ")]
    public void Missing_folder_is_an_error(string arg)
    {
        Assert.False(ProfileDirectoryOption.TryParse([arg], Base, out var directory, out var error));
        Assert.Null(directory);
        Assert.Equal(ProfileDirectoryOption.MissingFolder, error);
    }

    [Fact]
    public void Unknown_option_is_an_error()
    {
        Assert.False(ProfileDirectoryOption.TryParse(["--nope"], Base, out var directory, out var error));
        Assert.Null(directory);
        Assert.Equal("Unknown option: --nope.", error);
    }

    [Fact]
    public void Unexpected_argument_is_an_error()
    {
        Assert.False(ProfileDirectoryOption.TryParse(["hello"], Base, out var directory, out var error));
        Assert.Null(directory);
        Assert.Equal("Unexpected argument: hello.", error);
    }

    [Fact]
    public void Default_folder_keeps_an_empty_suffix()
    {
        var def = AppPaths.DefaultDirectory;
        Assert.Equal("", ProfileDirectoryOption.InstanceSuffix(def));
        Assert.Equal("", ProfileDirectoryOption.InstanceSuffix(def + Path.DirectorySeparatorChar));
        Assert.Equal("", ProfileDirectoryOption.InstanceSuffix(def.ToLowerInvariant()));
    }

    [Fact]
    public void Custom_folders_hash_without_case_or_trailing_slash()
    {
        var foo = ProfileDirectoryOption.InstanceSuffix(@"C:\Foo");
        Assert.Equal(foo, ProfileDirectoryOption.InstanceSuffix(@"c:\foo\"));
        Assert.Matches(new Regex(@"^\.[0-9A-F]{16}$"), foo);
        Assert.NotEqual(foo, ProfileDirectoryOption.InstanceSuffix(@"C:\Bar"));
    }
}
