using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InteractiveMap;

public enum AppTheme { Dark, Light, Dim }

/// <summary>
/// Three looks. Dark and Dim both use the dark control set; Dim swaps the
/// near-black surfaces for a softer blue-grey.
/// </summary>
public static class Theme
{
    sealed record Palette(string Bg, string Pane, string Card, string Stroke, string Hover, string Text, string TextDim);

    static readonly Palette DarkP = new("#0B0E11", "#12161B", "#171C22", "#262D36", "#1E252D", "#F2F4F6", "#9AA4AF");
    static readonly Palette DimP = new("#15202B", "#1B2733", "#22303C", "#38444D", "#2A3947", "#F7F9F9", "#8B98A5");
    static readonly Palette LightP = new("#F3F3F3", "#FAFAFA", "#FFFFFF", "#DDDFE2", "#ECEDEF", "#1B1B1B", "#5F6368");

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static AppTheme Parse(string? name) =>
        Enum.TryParse<AppTheme>(name, true, out var t) ? t : AppTheme.Dark;

    public static void Apply(AppTheme theme, FrameworkElement root, AppWindow window)
    {
        Current = theme;
        var p = theme switch { AppTheme.Light => LightP, AppTheme.Dim => DimP, _ => DarkP };
        Set("AppBgBrush", p.Bg);
        Set("PaneBgBrush", p.Pane);
        Set("CardBgBrush", p.Card);
        Set("StrokeBrush", p.Stroke);
        Set("HoverBrush", p.Hover);
        Set("TextBrush", p.Text);
        Set("TextDimBrush", p.TextDim);

        root.RequestedTheme = theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark;

        // the system-drawn minimise / maximise / close buttons
        var text = Hex.Parse(p.Text, Colors.White);
        var bar = window.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = text;
        bar.ButtonInactiveForegroundColor = Hex.Parse(p.TextDim, Colors.Gray);
        bar.ButtonHoverBackgroundColor = Hex.Parse(p.Hover, Colors.Gray);
        bar.ButtonHoverForegroundColor = text;
        bar.ButtonPressedBackgroundColor = Hex.Parse(p.Stroke, Colors.Gray);
        bar.ButtonPressedForegroundColor = text;
    }

    public static void SetAccent(Color accent) => Brush("AccentBrush").Color = accent;

    public static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];

    static void Set(string key, string hex) => Brush(key).Color = Hex.Parse(hex, Colors.Black);
}
