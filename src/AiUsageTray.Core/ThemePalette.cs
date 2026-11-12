namespace AiUsageTray.Core;

/// <summary>Single source of the UI colors. The app applies it and a test checks its contrast, so they cannot drift apart.</summary>
public static class ThemePalette
{
    public sealed record Entry(string Key, string Light, string Dark);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("BackgroundBrush", "#FFFFFF", "#292929"), new("SoftBrush", "#F5F5F5", "#2E2E2E"),
        new("TextBrush", "#242424", "#DEDEDE"), new("MutedBrush", "#5C5C5C", "#B0B0B0"),
        new("BorderBrush", "#DEDEDE", "#474747"), new("AccentBrush", "#B11F4B", "#FD8EA1"),
        new("WarningBrush", "#B45309", "#FBBF24")
    ];

    public static string Color(string key, bool dark)
    {
        var entry = Entries.Single(e => e.Key == key);
        return dark ? entry.Dark : entry.Light;
    }

    /// <summary>WCAG 2.x contrast ratio between two #RRGGBB colors.</summary>
    public static double Contrast(string first, string second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int start)
        {
            var value = Convert.ToInt32(hex.Substring(start, 2), 16) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
