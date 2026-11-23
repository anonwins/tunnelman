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

        return settings;
    }

    private static void CopyInto(UiSettings from, UiSettings to)
    {
        to.NormalLeft = from.NormalLeft;
        to.NormalTop = from.NormalTop;
        to.NormalRight = from.NormalRight;
        to.NormalBottom = from.NormalBottom;
        to.Maximized = from.Maximized;
    }

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
}
