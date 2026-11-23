using Tunnelman;

namespace Tunnelman.Tests;

public sealed class UiSettingsTests
{
    [Fact]
    public void Old_lock_keys_do_not_change_window_bounds()
    {
        var dir = SessionTestHost.NewDir("tunnelman-ui-");
        var file = Path.Combine(dir, "ui.json");
        try
        {
            File.WriteAllText(file, """
                {
                  "normalLeft": 10,
                  "normalTop": 20,
                  "normalRight": 30,
                  "normalBottom": 40,
                  "maximized": true,
                  "autoLockSeconds": 60,
                  "lockOnTray": true,
                  "stopDialingWhenLocked": true
                }
                """);
            var loaded = UiSettings.LoadFrom(file);
            Assert.Equal(10, loaded.NormalLeft);
            Assert.Equal(20, loaded.NormalTop);
            Assert.Equal(30, loaded.NormalRight);
            Assert.Equal(40, loaded.NormalBottom);
            Assert.True(loaded.Maximized);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
