using System.Globalization;

namespace AiUsageTray.Core;

public static class AmountFormat
{
    /// <summary>
    /// Short, culture-independent form for narrow surfaces: 66,987 becomes 67k and 1,234,567 becomes 1.2M.
    /// Values under 1,000 are shown as they are.
    /// </summary>
    public static string Compact(decimal value)
    {
        var sign = value < 0 ? "-" : "";
        var magnitude = Math.Abs(value);
        if (magnitude < 1000)
        {
            var rounded = Math.Round(magnitude, 1, MidpointRounding.AwayFromZero);
            if (rounded < 1000)
                return sign + Trim(rounded);
        }
        // Rounding must not leave a value such as 1000k; promote to the next unit instead.
        var thousands = Math.Round(magnitude / 1000, magnitude < 100_000 ? 1 : 0, MidpointRounding.AwayFromZero);
        if (thousands < 1000)
            return sign + Trim(thousands) + "k";
        return sign + Trim(Math.Round(magnitude / 1_000_000, magnitude < 100_000_000 ? 1 : 0, MidpointRounding.AwayFromZero)) + "M";
    }

    private static string Trim(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
