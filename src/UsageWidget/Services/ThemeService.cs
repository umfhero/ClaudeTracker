using System.Windows;
using System.Windows.Media;

namespace UsageWidget.Services;

/// <summary>
/// Swaps the app-level brush resources between the dark and light palettes.
/// Windows reference them via DynamicResource, so a swap restyles everything live.
/// Status colors (amber/red/green) are fixed and never themed.
/// </summary>
public static class ThemeService
{
    public static void Apply(bool light)
    {
        var r = Application.Current.Resources;
        Set(r, "CardBg", light ? "#F2FCFCFB" : "#F21A1A19");
        Set(r, "CardBorder", light ? "#1A0B0B0B" : "#1AFFFFFF");
        Set(r, "InkPrimary", light ? "#0B0B0B" : "#FFFFFF");
        Set(r, "InkSecondary", light ? "#52514E" : "#C3C2B7");
        Set(r, "InkMuted", "#898781");
        Set(r, "Track", light ? "#E1E0D9" : "#2C2C2A");
        Set(r, "Accent", light ? "#C15F3C" : "#D97757");
        Set(r, "AccentSoft", light ? "#40C15F3C" : "#40D97757");

        var accent = (Color)ColorConverter.ConvertFromString(light ? "#C15F3C" : "#D97757");
        var fadeEnd = Color.FromArgb(0, accent.R, accent.G, accent.B);
        var fade = new LinearGradientBrush(accent, fadeEnd, new Point(0, 0), new Point(1, 0));
        fade.Freeze();
        r["AccentFade"] = fade;
    }

    private static void Set(ResourceDictionary r, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        r[key] = brush;
    }
}
