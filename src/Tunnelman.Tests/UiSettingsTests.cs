using Tunnelman;

namespace Tunnelman.Tests;

public sealed class UiSettingsTests
{
    [Fact]
    public void Missing_tray_lock_follows_an_idle_timeout()
    {
        var dir = SessionTestHost.NewDir("tunnelman-ui-");
        var file = Path.Combine(dir, "ui.json");
        try
        {
            File.WriteAllText(file, """{ "autoLockSeconds": 60 }""");
            var inferred = UiSettings.LoadFrom(file);
            Assert.Equal(60, inferred.AutoLockSeconds);
            Assert.True(inferred.LockWhenHidden);
            Assert.False(inferred.StopDialingWhenLocked);

            File.WriteAllText(file, """{ "autoLockSeconds": 60, "lockOnTray": false }""");
            var explicitOff = UiSettings.LoadFrom(file);
            Assert.Equal(60, explicitOff.AutoLockSeconds);
            Assert.False(explicitOff.LockWhenHidden);

            explicitOff.ApplyLockSettings(0, true, true);
            var saved = UiSettings.LoadFrom(file);
            Assert.Equal(0, saved.AutoLockSeconds);
            Assert.True(saved.LockWhenHidden);
            Assert.True(saved.StopDialingWhenLocked);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
