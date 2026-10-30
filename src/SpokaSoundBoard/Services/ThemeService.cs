using System.Windows;
using System.Windows.Media;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Services;

public sealed record AccentPreset(string Name, string Accent, string Secondary);

/// <summary>Applies the user's appearance settings by replacing the dynamic brushes of the application.</summary>
public static class ThemeService
{
    public static IReadOnlyList<AccentPreset> Accents { get; } =
    [
        new("Violet", "#7C5CFF", "#22D3EE"),
        new("Bleu", "#3B82F6", "#22D3EE"),
        new("Cyan", "#06B6D4", "#A78BFA"),
        new("Vert", "#10B981", "#A3E635"),
        new("Orange", "#F97316", "#FACC15"),
        new("Rouge", "#EF4444", "#FB923C"),
        new("Rose", "#EC4899", "#A78BFA"),
        new("Or", "#EAB308", "#F97316"),
        new("Gris", "#64748B", "#94A3B8"),
    ];

    private sealed record Palette(
        string Window, string Sidebar, string Card, string CardHover, string CardBorder, string Text, string SubtleText,
        string Control, string SubCard, string Inset, string ControlBorder, string ControlBorderHover, string ToggleOff,
        string ToggleOffBorder, string ToggleKnob, string HoverOverlay, string Toast, string IdleDot, string MeterTrack,
        string WarningSoft);

    private static readonly Palette Dark = new(
        "#0F1117", "#141722", "#1A1E2B", "#222738", "#2A3042", "#F1F3F9", "#9AA3B8",
        "#262B3B", "#20263A", "#33000000", "#343B50", "#56607A", "#2C3245",
        "#4A5268", "#C9CFDD", "#1EFFFFFF", "#F0262B3B", "#3A4155", "#262B3B", "#26F59E0B");

    private static readonly Palette Oled = new(
        "#000000", "#06070A", "#0D0F14", "#161922", "#1E2230", "#F5F6FA", "#8E96AA",
        "#171A23", "#11141C", "#40000000", "#2A2F3D", "#4A5268", "#20242F",
        "#3E4555", "#C9CFDD", "#18FFFFFF", "#F0171A23", "#2E3442", "#1A1D26", "#26F59E0B");

    private static readonly Palette Light = new(
        "#F4F5F9", "#FFFFFF", "#FFFFFF", "#F0F1F6", "#E1E4EC", "#161A26", "#5B6478",
        "#ECEEF4", "#F6F7FB", "#0F000000", "#D5D9E3", "#AEB5C5", "#D7DBE5",
        "#B9BFCC", "#FFFFFF", "#12000000", "#F2FFFFFF", "#C5CAD6", "#E3E6EE", "#33F59E0B");

    public static void Apply(AppearanceSettings appearance)
    {
        var resources = Application.Current.Resources;
        var palette = appearance.Theme switch
        {
            AppTheme.Oled => Oled,
            AppTheme.Light => Light,
            _ => Dark,
        };

        Set(resources, "WindowBrush", palette.Window);
        Set(resources, "SidebarBrush", palette.Sidebar);
        Set(resources, "CardBrush", palette.Card);
        Set(resources, "CardHoverBrush", palette.CardHover);
        Set(resources, "CardBorderBrush", palette.CardBorder);
        Set(resources, "TextBrush", palette.Text);
        Set(resources, "SubtleTextBrush", palette.SubtleText);
        Set(resources, "ControlBrush", palette.Control);
        Set(resources, "SubCardBrush", palette.SubCard);
        Set(resources, "InsetBrush", palette.Inset);
        Set(resources, "ControlBorderBrush", palette.ControlBorder);
        Set(resources, "ControlBorderHoverBrush", palette.ControlBorderHover);
        Set(resources, "ToggleOffBrush", palette.ToggleOff);
        Set(resources, "ToggleOffBorderBrush", palette.ToggleOffBorder);
        Set(resources, "ToggleKnobBrush", palette.ToggleKnob);
        Set(resources, "HoverOverlayBrush", palette.HoverOverlay);
        Set(resources, "ToastBrush", palette.Toast);
        Set(resources, "IdleDotBrush", palette.IdleDot);
        Set(resources, "MeterTrackBrush", palette.MeterTrack);
        Set(resources, "WarningSoftBrush", palette.WarningSoft);

        var accent = Parse(appearance.AccentColor, "#7C5CFF");
        var secondary = Parse(appearance.SecondaryColor, "#22D3EE");
        // Light backgrounds need a darker secondary color for readable text.
        if (appearance.Theme == AppTheme.Light) secondary = Darken(secondary, 0.35);

        resources["AccentColor"] = accent;
        resources["Accent2Color"] = secondary;
        resources["AccentBrush"] = Frozen(new SolidColorBrush(accent));
        resources["Accent2Brush"] = Frozen(new SolidColorBrush(secondary));
        resources["AccentSoftBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)));
        resources["BrandBrush"] = Frozen(new LinearGradientBrush(accent, Parse(appearance.SecondaryColor, "#22D3EE"), new Point(0, 0), new Point(1, 1)));

        var (width, height) = appearance.PadSize switch
        {
            PadSize.Small => (140d, 96d),
            PadSize.Large => (220d, 150d),
            _ => (176d, 122d),
        };
        resources["PadWidth"] = width;
        resources["PadHeight"] = height;

#pragma warning disable WPF0001
        Application.Current.ThemeMode = appearance.Theme == AppTheme.Light ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
    }

    public static Color Parse(string? hex, string fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? fallback : hex.Trim());
        }
        catch (FormatException)
        {
            return (Color)ColorConverter.ConvertFromString(fallback);
        }
    }

    public static bool IsValidColor(string? hex) =>
        hex != null && System.Text.RegularExpressions.Regex.IsMatch(hex.Trim(), "^#([0-9A-Fa-f]{6})$");

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)), (byte)(c.G * (1 - amount)), (byte)(c.B * (1 - amount)));

    private static void Set(ResourceDictionary resources, string key, string hex) =>
        resources[key] = Frozen(new SolidColorBrush(Parse(hex, "#FF00FF")));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
