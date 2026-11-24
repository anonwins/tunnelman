using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Tunnelman;

internal static class CopyFeedback
{
    private static readonly TimeSpan TextFlashDuration = TimeSpan.FromMilliseconds(700);
    private static readonly ConditionalWeakTable<TextBlock, DispatcherTimer> TextFlashes = new();

    public static bool TryCopy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            return false;
        }

        return true;
    }

    public static void FlashText(TextBlock text)
    {
        StopTextFlash(text);

        var from = (System.Windows.Media.Color)Application.Current.Resources["TextColor"];
        var to = (System.Windows.Media.Color)Application.Current.Resources["MutedColor"];
        var brush = new SolidColorBrush(from);
        text.Foreground = brush;

        var anim = new ColorAnimation(from, to, new Duration(TextFlashDuration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);

        var timer = new DispatcherTimer { Interval = TextFlashDuration };
        timer.Tick += (_, _) => StopTextFlash(text);
        text.Unloaded += OnTextUnloaded;
        text.DataContextChanged += OnTextDataContextChanged;
        TextFlashes.Add(text, timer);
        timer.Start();
    }

    private static void OnTextUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock text)
            StopTextFlash(text);
    }

    private static void OnTextDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBlock text)
            StopTextFlash(text);
    }

    private static void StopTextFlash(TextBlock text)
    {
        if (TextFlashes.TryGetValue(text, out var timer))
        {
            timer.Stop();
            TextFlashes.Remove(text);
        }

        text.Unloaded -= OnTextUnloaded;
        text.DataContextChanged -= OnTextDataContextChanged;
        text.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedBrush"];
    }
}
