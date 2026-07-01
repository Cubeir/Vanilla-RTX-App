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
}
