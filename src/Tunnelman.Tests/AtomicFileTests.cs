using Tunnelman;

namespace Tunnelman.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public void First_create_and_replace_leave_no_tmp_or_prev()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-atomic-" + Guid.NewGuid().ToString("n"));
        var file = Path.Combine(dir, "tunnels.json");
        try
        {
            AtomicFile.WriteAllText(file, "{\"a\":1}");
            Assert.True(File.Exists(file));
            Assert.Equal("{\"a\":1}", File.ReadAllText(file));
            AssertNoSiblings(file);

            AtomicFile.WriteAllText(file, "{\"a\":2}");
            Assert.Equal("{\"a\":2}", File.ReadAllText(file));
            AssertNoSiblings(file);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Recover_moves_tmp_onto_vanished_dest_and_does_not_throw()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-atomic-rec-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        var tmp = file + "." + Guid.NewGuid().ToString("n") + ".tmp";
        File.WriteAllText(tmp, "new");
        try
        {
            Assert.True(AtomicFile.TryRecover(file, tmp, backup: null));
            Assert.Equal("new", File.ReadAllText(file));
            Assert.False(File.Exists(tmp));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Recover_restores_prev_when_dest_and_tmp_are_gone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelman-atomic-prev-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "tunnels.json");
        var tmp = file + "." + Guid.NewGuid().ToString("n") + ".tmp";
        var prev = file + "." + Guid.NewGuid().ToString("n") + ".prev";
        File.WriteAllText(prev, "old");
        try
        {
            Assert.False(AtomicFile.TryRecover(file, tmp, prev));
            Assert.Equal("old", File.ReadAllText(file));
            Assert.False(File.Exists(prev));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void AssertNoSiblings(string file)
    {
        var name = Path.GetFileName(file);
        var dir = Path.GetDirectoryName(file)!;
        Assert.Empty(Directory.GetFiles(dir, name + ".*.tmp"));
        Assert.Empty(Directory.GetFiles(dir, name + ".*.prev"));
        Assert.False(File.Exists(file + ".tmp"));
        Assert.False(File.Exists(file + ".bak"));
    }
}
