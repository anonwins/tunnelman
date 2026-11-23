using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Tunnelman;

internal sealed class UiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static readonly (int Seconds, string Label)[] AutoLockChoices =
    [
        (0, "Don't auto-lock"),
        (30, "After 30 seconds idle"),
        (60, "After 1 minute idle"),
        (300, "After 5 minutes idle"),
        (900, "After 15 minutes idle"),
    ];

    public static UiSettings Current { get; internal set; } = new();

    private readonly string _filePath;

    public UiSettings()
        : this(AppPaths.UiFile)
    {
    }

    internal UiSettings(string filePath)
    {
        _filePath = filePath;
    }

    [JsonPropertyName("normalLeft")]
    public int NormalLeft { get; set; }

    [JsonPropertyName("normalTop")]
    public int NormalTop { get; set; }

    [JsonPropertyName("normalRight")]
    public int NormalRight { get; set; }

    [JsonPropertyName("normalBottom")]
    public int NormalBottom { get; set; }

    [JsonPropertyName("maximized")]
    public bool Maximized { get; set; }

    [JsonPropertyName("autoLockSeconds")]
    public int AutoLockSeconds { get; set; }

    [JsonPropertyName("lockOnTray")]
    public bool? LockOnTray { get; set; }

    [JsonPropertyName("stopDialingWhenLocked")]
    public bool StopDialingWhenLocked { get; set; }

    public bool LockWhenHidden => LockOnTray == true;

    public static void Load()
    {
        var settings = new UiSettings();
        try
        {
            if (File.Exists(settings._filePath))
            {
                var parsed = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(settings._filePath), JsonOptions);
                if (parsed is not null)
                    CopyInto(parsed, settings);
            }
        }
        catch
        {
            // keep defaults
        }

        Finish(settings);
        Current = settings;
    }

    internal static UiSettings LoadFrom(string filePath)
    {
        var settings = new UiSettings(filePath);
        try
        {
            if (File.Exists(filePath))
            {
                var parsed = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(filePath), JsonOptions);
                if (parsed is not null)
                    CopyInto(parsed, settings);
            }
        }
        catch
        {
            // keep defaults
        }

        Finish(settings);
        return settings;
    }

    private static void CopyInto(UiSettings from, UiSettings to)
    {
        to.NormalLeft = from.NormalLeft;
        to.NormalTop = from.NormalTop;
        to.NormalRight = from.NormalRight;
        to.NormalBottom = from.NormalBottom;
        to.Maximized = from.Maximized;
        to.AutoLockSeconds = from.AutoLockSeconds;
        to.LockOnTray = from.LockOnTray;
        to.StopDialingWhenLocked = from.StopDialingWhenLocked;
    }

    private static void Finish(UiSettings settings)
    {
        settings.AutoLockSeconds = NormalizeAutoLock(settings.AutoLockSeconds);
        settings.LockOnTray ??= settings.AutoLockSeconds > 0;
    }

    public static int NormalizeAutoLock(int seconds) => seconds switch
    {
        30 or 60 or 300 or 900 => seconds,
        _ => 0,
    };

    public void Save()
    {
        try
        {
            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(this, JsonOptions));
            AppPaths.TryProtectWrittenFile(_filePath);
        }
        catch
        {
            // settings are non-critical
        }
    }

    public void Capture(Window window)
    {
        if (!NativeWindowPlacement.TryCapture(window, out var bounds, out var maximized))
            return;
        NormalLeft = bounds.Left;
        NormalTop = bounds.Top;
        NormalRight = bounds.Right;
        NormalBottom = bounds.Bottom;
        Maximized = maximized;
    }

    public void Apply(Window window)
    {
        var bounds = new NativeWindowPlacement.RectPixels(NormalLeft, NormalTop, NormalRight, NormalBottom);
        NativeWindowPlacement.TryApply(window, bounds, Maximized);
    }

    public void ApplyLockSettings(int autoLockSeconds, bool lockOnTray, bool stopDialingWhenLocked)
    {
        AutoLockSeconds = NormalizeAutoLock(autoLockSeconds);
        LockOnTray = lockOnTray;
        StopDialingWhenLocked = stopDialingWhenLocked;
        Save();
    }
}
