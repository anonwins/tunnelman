using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TunnelItemLogTests
{
    private static TunnelItem Item() => new(new TunnelRecord
    {
        Id = Guid.NewGuid(),
        Name = "log",
    });

    [Fact]
    public void AppendLog_joins_without_trailing_newline()
    {
        var item = Item();
        item.AppendLog("a");
        Assert.Equal("a", item.LogText);

        item.AppendLog("b");
        Assert.Equal(string.Join(Environment.NewLine, "a", "b"), item.LogText);
        Assert.False(item.LogText.EndsWith(Environment.NewLine, StringComparison.Ordinal));
    }

    [Fact]
    public void AppendLog_trims_oldest_and_still_has_no_trailing_newline()
    {
        var item = Item();
        for (var i = 0; i < TunnelItem.MaxLogLines + 1; i++)
            item.AppendLog($"L{i}");

        var expected = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, TunnelItem.MaxLogLines).Select(i => $"L{i}"));

        Assert.Equal(expected, item.LogText);
        Assert.False(item.LogText.EndsWith(Environment.NewLine, StringComparison.Ordinal));
    }
}
