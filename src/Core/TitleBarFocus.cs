using Microsoft.UI.Xaml;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Fades a window's custom titlebar content when the window loses focus, so our own
/// titlebar tracks the system's.
///
/// Windows already dims its caption buttons (minimize/maximize/close) on an inactive
/// window. Every window in this app draws its own titlebar content next to those - buttons
/// in the main window and Alchitex, a centered title everywhere - and without this that
/// content stayed at full strength while the system's half of the same bar dimmed, which
/// reads as the titlebar being two mismatched pieces. UnfocusedOpacity is matched to the
/// caption buttons by eye and holds up in both light and dark themes.
///
/// Attach whole containers, not individual controls. The main window used to assign the
/// same opacity to five named buttons one at a time, which meant a sixth titlebar button
/// silently didn't fade until someone remembered to add a line. Give the group a name in
/// XAML and hand that over instead - anything added inside it inherits this for free.
///
/// One thing deliberately stays out of it: the centered title text. It's a separate
/// element from the button group in both windows that have both, and the main window's
/// title is the one piece of titlebar content that never fades at all - it's the app's
/// identity, not a control. Alchitex's title does fade, because there it doubles as the
/// generation status line rather than a permanent label.
/// </summary>
public static class TitleBarFocus
{
    public const double FocusedOpacity = 1.0;
    public const double UnfocusedOpacity = 0.5;

    /// <summary>
    /// Ties the given elements' opacity to <paramref name="window"/>'s focus for the life
    /// of the window, and applies the current state immediately.
    ///
    /// The handler is never detached: it only captures the window's own children, so it
    /// dies with the window. A collapsed element is fine to pass (Alchitex's titlebar
    /// buttons are hidden until its license is accepted) - opacity is applied regardless
    /// and is simply already correct once it's shown.
    /// </summary>
    public static void Attach(Window window, params UIElement[] elements)
    {
        if (window == null || elements == null || elements.Length == 0) return;

        window.Activated += (s, e) =>
            Apply(elements, e.WindowActivationState != WindowActivationState.Deactivated);

        Apply(elements, isFocused: true);
    }

    private static void Apply(UIElement[] elements, bool isFocused)
    {
        var opacity = isFocused ? FocusedOpacity : UnfocusedOpacity;

        foreach (var element in elements)
            if (element != null)
                element.Opacity = opacity;
    }
}
