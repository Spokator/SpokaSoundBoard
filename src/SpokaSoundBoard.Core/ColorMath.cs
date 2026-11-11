namespace SpokaSoundBoard.Core;

/// <summary>WCAG contrast helpers, used to pick a readable text color on the user's accent color.</summary>
public static class ColorMath
{
    /// <summary>Relative luminance (0 = black, 1 = white) of an sRGB color.</summary>
    public static double Luminance(byte r, byte g, byte b)
    {
        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    /// <summary>Contrast ratio between two luminances, from 1 (none) to 21 (black on white).</summary>
    public static double Contrast(double l1, double l2) => (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);

    /// <summary>
    /// White stays the text color on the accent as long as it keeps a 3:1 contrast (the app's look); only light accents
    /// (gold, lime, cyan…) switch to near-black, when that reads better.
    /// </summary>
    public static bool PrefersDarkText(byte r, byte g, byte b)
    {
        var l = Luminance(r, g, b);
        var white = Contrast(l, 1.0);
        return white < 3.0 && Contrast(l, Luminance(0x11, 0x14, 0x1C)) > white;
    }
}
