using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Vanilla_RTX_App.Core;

public static class ThemeService
{
    public static event Action<ElementTheme>? ThemeChanged;

    public static void Broadcast(ElementTheme theme) => ThemeChanged?.Invoke(theme);

    public static void ApplyTitleBarColors(AppWindow appWindow, ElementTheme theme)
    {
        var titleBar = appWindow?.TitleBar;
        if (titleBar == null) return;

        bool isLight = theme == ElementTheme.Light;
        titleBar.ButtonForegroundColor = isLight ? Colors.Black : Colors.White;
        titleBar.ButtonHoverForegroundColor = isLight ? Colors.Black : Colors.White;
        titleBar.ButtonPressedForegroundColor = isLight ? Colors.Black : Colors.White;
        titleBar.ButtonInactiveForegroundColor = isLight
            ? Color.FromArgb(255, 128, 128, 128)
            : Color.FromArgb(255, 160, 160, 160);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = isLight
            ? Color.FromArgb(20, 0, 0, 0)
            : Color.FromArgb(40, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = isLight
            ? Color.FromArgb(40, 0, 0, 0)
            : Color.FromArgb(60, 255, 255, 255);
    }



    public enum BevelEdge { Left, Right }

    /// <summary>
    /// Color for one edge of the "fake split button" bevel decoration used around
    /// toggle-style buttons (target preview toggle, install/reinstall, etc).
    /// Left edge always reads the "bright" source, right edge the "dark" source —
    /// accented=true when the button represents an active/highlighted state
    /// (checked toggle, not-yet-installed/call-to-action), accented=false for the
    /// resting/default state.
    /// </summary>
    // Core/ThemeService.cs

    public static Color GetBevelColor(ElementTheme theme, BevelEdge edge, bool accented, bool isEnabled = true)
    {
        // Disabled state always falls back to the resting (non-accented) bevel,
        // dimmed, regardless of what the enabled state would've shown — this way
        // re-enabling just re-runs the normal accented/resting logic and the bevel
        // snaps back exactly as if nothing happened.
        if (!isEnabled)
        {
            var restingColor = GetRestingBevelColor(theme, edge);
            return Color.FromArgb(90, restingColor.R, restingColor.G, restingColor.B); // ~35% opacity, matches typical WinUI disabled dimming
        }

        if (accented)
        {
            var key = edge == BevelEdge.Left
                ? (theme == ElementTheme.Light ? "SystemAccentColorLight1" : "SystemAccentColorLight3")
                : (theme == ElementTheme.Light ? "SystemAccentColorDark2" : "SystemAccentColorDark1");
            return (Color)Application.Current.Resources[key];
        }

        return GetRestingBevelColor(theme, edge);
    }

    private static Color GetRestingBevelColor(ElementTheme theme, BevelEdge edge)
    {
        var themeKey = theme == ElementTheme.Light ? "Light" : "Dark";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var themeDictObj)
            && themeDictObj is ResourceDictionary dict)
        {
            var resKey = edge == BevelEdge.Left ? "FakeSplitButtonBrightBorderColor" : "FakeSplitButtonDarkBorderColor";
            if (dict.TryGetValue(resKey, out var colorObj) && colorObj is Color color)
                return color;
        }
        return Colors.Transparent;
    }
}
