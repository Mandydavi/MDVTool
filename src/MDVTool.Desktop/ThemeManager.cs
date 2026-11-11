using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace MDVTool;

/// <summary>
/// 主题切换：整体替换 Application.Resources.MergedDictionaries[0] 的调色板，
/// 再按所选主色覆盖强调色。样式里所有颜色都用 DynamicResource 引用，替换后立即刷新。
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    /// <summary>主题或配色变化后触发，用于同步系统窗口边框</summary>
    public static event Action? ThemeChanged;
    public static string Palette { get; private set; } = "blue";

    private sealed record PaletteSet(string Accent, string Accent2, string Hover);

    private static readonly (string Name, PaletteSet Dark, PaletteSet Light)[] Sets =
    {
        ("blue",   new PaletteSet("#6AA9FF", "#4C7DF0", "#8ABEFF"), new PaletteSet("#2563EB", "#1D4ED8", "#3B82F6")),
        ("mint",   new PaletteSet("#8BE28B", "#4FC08A", "#A6EBA6"), new PaletteSet("#15803D", "#0F6E36", "#1C9A4B")),
        ("violet", new PaletteSet("#B49BFF", "#8B72F0", "#C8B6FF"), new PaletteSet("#7C3AED", "#6D28D9", "#8B5CF6")),
        ("coral",  new PaletteSet("#FF9B7A", "#F0704F", "#FFB296"), new PaletteSet("#D9542B", "#BE441F", "#E4673C")),
    };

    public static void Apply(bool dark) => Apply(dark, Palette);

    public static void Apply(bool dark, string palette)
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged == null) return;

        var dict = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Palette.Dark.xaml" : "Themes/Palette.Light.xaml", UriKind.Relative)
        };

        var set = Sets.FirstOrDefault(x => x.Name == palette);
        if (set.Name == null) set = Sets[0];
        var p = dark ? set.Dark : set.Light;

        var accent = (Color)ColorConverter.ConvertFromString(p.Accent);
        var accent2 = (Color)ColorConverter.ConvertFromString(p.Accent2);
        var hover = (Color)ColorConverter.ConvertFromString(p.Hover);
        var bg = (Color)ColorConverter.ConvertFromString(dark ? "#0A0C10" : "#F4F6FA");

        dict["Accent"] = new SolidColorBrush(accent);
        dict["Accent2"] = new SolidColorBrush(accent2);
        dict["AccentHover"] = new SolidColorBrush(hover);
        dict["AccentSoft"] = new SolidColorBrush(Blend(accent, bg, dark ? 0.14 : 0.10));
        dict["BrandSoft"] = new SolidColorBrush(Blend(accent, bg, dark ? 0.14 : 0.10));
        dict["AccentDisabled"] = new SolidColorBrush(Blend(accent, bg, dark ? 0.34 : 0.30));
        dict["AccentRing"] = new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B));
        dict["BrandGrad"] = new LinearGradientBrush(accent, accent2, new Point(0, 0), new Point(1, 1));

        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;

        IsDark = dark;
        Palette = set.Name;
        ThemeChanged?.Invoke();
    }

    private static Color Blend(Color fg, Color bg, double a)
    {
        byte M(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(M(fg.R, bg.R), M(fg.G, bg.G), M(fg.B, bg.B));
    }
}
