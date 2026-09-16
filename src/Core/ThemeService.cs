using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// The app's one source of theme truth, and the two things XAML cannot theme on its own:
/// the system-drawn caption buttons, and colours built in code rather than bound in markup.
///
/// <para>Every window subscribes to <see cref="ThemeChanged"/> in its constructor and
/// unsubscribes when it closes. MainWindow is the only thing that <see cref="Broadcast"/>s;
/// child windows follow. Anything resolving a colour once at construction - runtime-built
/// controls with no ThemeResource binding - has to follow the event too, or it keeps the
/// colour it was born with for the rest of the session.</para>
/// </summary>
public static class ThemeService
{
    /// <summary>
    /// Raised when the user changes the app theme. Subscribe in a window's constructor,
    /// unsubscribe in its Closed handler - a window that stays subscribed after closing
    /// throws when the event fires against its dead visual tree.
    /// </summary>
    public static event Action<ElementTheme>? ThemeChanged;

    /// <summary>Tells every open window a theme change has happened. MainWindow's to call.</summary>
    public static void Broadcast(ElementTheme theme) => ThemeChanged?.Invoke(theme);

    /// <summary>
    /// Recolours the system-drawn caption buttons to match <paramref name="theme"/>.
    ///
    /// <para>Needed because those buttons sit outside the XAML tree: every window in this app
    /// extends its content into the titlebar, so the system keeps drawing minimize/maximize/
    /// close itself and they stay at the OS theme unless set here. Backgrounds are transparent
    /// so the window's own backdrop shows through; only the hover and pressed states paint,
    /// and they are alpha-blended for the same reason.</para>
    ///
    /// <para>Call it from a window's constructor and again on every
    /// <see cref="ThemeChanged"/>. Safe on a window with no titlebar - it no-ops.</para>
    /// </summary>
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

    /// <summary>
    /// The persisted theme choice as an <see cref="ElementTheme"/>, for a window setting
    /// <c>RequestedTheme</c> at construction. Anything unrecognised - including no stored
    /// value at all - resolves to <see cref="ElementTheme.Default"/>, which is "follow
    /// Windows".
    /// </summary>
    public static ElementTheme ResolveInitialTheme() =>
    (EnvironmentVariables.Persistent.AppThemeMode ?? "System") switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>Which side of a fake-split-button seam a bevel colour is for.</summary>
    public enum BevelEdge { Left, Right }

    /// <summary>
    /// Colour for one edge of the "fake split button" bevel - the 3px strips that make two
    /// adjacent buttons read as one split control (the target-preview toggle, install/
    /// reinstall, the preset rows' delete button).
    ///
    /// <para>Left edge always takes the bright source and right edge the dark one, which is
    /// what gives the seam its raised look. <paramref name="accented"/> is true for an
    /// active or call-to-action state (a checked toggle, a not-yet-installed preset) and
    /// false at rest.</para>
    ///
    /// <para><b>For code-built controls only.</b> XAML uses the
    /// <c>FakeSplitButton*BorderColor</c> ThemeResources directly and re-resolves them on a
    /// theme change for free; anything calling this resolves once, so it must also follow
    /// <see cref="ThemeChanged"/> and call again.</para>
    /// </summary>
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

    /// <summary>
    /// The non-accented bevel colour, read out of the theme dictionaries by hand.
    ///
    /// <para><see cref="Application.Current"/>'s resource lookup resolves against the app's
    /// <i>current</i> theme, which is not necessarily the one being asked about - a window
    /// can be rendering Light while the app is Dark. Indexing ThemeDictionaries explicitly is
    /// what makes the answer depend on <paramref name="theme"/> rather than on timing.
    /// Transparent when the key is missing, so a typo shows as a missing bevel rather than an
    /// exception.</para>
    /// </summary>
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
