using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.Overlays;

namespace Vanilla_RTX_App;

/// <summary>
/// Opening and closing the feature modules, which are overlays over this window rather than
/// windows of their own.
///
/// <para><b>One module at a time, and the host is empty in between.</b> Opening a second while
/// one is up is not reachable - a module covers the body, which is where every button that
/// opens one lives - so this asserts that rather than arbitrating it.</para>
///
/// <para><b>The lifecycle is deliberately a window's lifecycle</b> (see
/// <see cref="ModuleOverlay"/> for the full reasoning): constructed on open, torn down on
/// close. The visible gain over the windows these replace is not that state survives a close -
/// it doesn't, and shouldn't - it is that the log, the lamp, the progress bar and the titlebar
/// stay on screen the whole time a module is open, and that Help and Bugs can be read over a
/// module without disturbing it.</para>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The open module, if any. A list rather than a single field because the file-activation
    /// router asks "is a window of this type open" (<see cref="FindOpenModule"/>) and reads
    /// better against a collection, and because one is exactly as cheap as the other here.
    /// </summary>
    private readonly List<ModuleOverlay> _openModules = new();

    /// <summary>
    /// This window's content as a <see cref="FrameworkElement"/> - a XamlRoot, a dispatcher
    /// and a theme, which is all anything that used to take the whole Window actually wanted
    /// from it. <see cref="Modules.ImportDialogs"/> is the case that made it worth naming: a
    /// module is a control now and cannot hand one a window.
    /// </summary>
    internal FrameworkElement RootElement => (FrameworkElement)Content;

    /// <summary>
    /// Puts <paramref name="overlay"/> on screen, locks <paramref name="toDisable"/> for as
    /// long as it is up, and runs <paramref name="onClosed"/> once it goes away.
    ///
    /// <para><b>The lock is taken here rather than at the call site</b> so its release cannot
    /// be forgotten: the two halves are the same pair of lines in one method, and a module that
    /// closes itself, is closed by its button, or throws on the way up all reach the same
    /// release. <see cref="LockControls"/> adds <c>SettingsButton</c> on top of the named
    /// controls, for the reason given on it.</para>
    ///
    /// <para><paramref name="onClosed"/> runs before the fade-out finishes, which is what keeps
    /// a module's closing log line and its re-enabled buttons in the same moment the user let
    /// go of it.</para>
    /// </summary>
    internal void OpenModule(ModuleOverlay overlay, string[] toDisable, Action<ModuleOverlay>? onClosed = null)
    {
        LockControls(false, toDisable);

        overlay.Closed += (_, _) =>
        {
            _openModules.Remove(overlay);
            ReleaseModuleTitleBarStrip(overlay);
            ShowModuleReturnButton(false);

            // Anything the module was still holding on its own controls goes with it. Those
            // controls are about to leave the visual tree, and a count left on one of them is
            // a count nothing will ever release.
            WindowControlsManager.ClearStates(overlay);

            LockControls(true, toDisable);

            onClosed?.Invoke(overlay);
        };

        _openModules.Add(overlay);
        AdoptModuleTitleBarStrip(overlay);
        ShowModuleReturnButton(true);
        ModuleOverlayHost.Children.Add(overlay);
        overlay.Show();
    }

    /// <summary>
    /// Logs a module's result the way every one of these has reported since they were windows:
    /// its own log level on success, Error on a failure that had something to say, and silence
    /// when the message is empty.
    /// </summary>
    internal void LogModuleResult(ModuleOverlay overlay, LogLevel level)
    {
        if (overlay.OperationSuccessful)
        {
            if (!string.IsNullOrEmpty(overlay.StatusMessage))
                Log(overlay.StatusMessage, level);
        }
        else if (!string.IsNullOrEmpty(overlay.StatusMessage))
        {
            Log(overlay.StatusMessage, LogLevel.Error);
        }
    }

    /// <summary>
    /// The titlebar's return button closes whichever module is open. It is one button shared by
    /// all six rather than one per module, which is the whole reason it can sit in the titlebar
    /// and look like a caption button.
    /// </summary>
    private void ModuleReturnButton_Click(object sender, RoutedEventArgs e) =>
        _openModules.LastOrDefault()?.Close();

    /// <summary>Shows or hides the return button and the hairline that separates it from the system's own caption buttons.</summary>
    private void ShowModuleReturnButton(bool show)
    {
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ModuleReturnButton.Visibility = visibility;
        ModuleReturnSeparator.Visibility = visibility;
    }

    /// <summary>
    /// Hosts a module's titlebar buttons in this window's titlebar for as long as it is open.
    ///
    /// <para><b>The module's own element, not a copy</b>, so its <c>x:Name</c> field and every
    /// <c>Click</c> handler on it keep working untouched - which is the whole reason those
    /// buttons are still declared in the module's own XAML. It arrives already detached from
    /// that XAML; see <see cref="ModuleOverlay.AttachChrome"/> for why that has to happen
    /// there rather than here.</para>
    /// </summary>
    private void AdoptModuleTitleBarStrip(ModuleOverlay overlay)
    {
        if (overlay.TitleBarStrip is not { } strip) return;

        ModuleTitleBarActions.Child = strip;
        ModuleTitleBarActions.Visibility = Visibility.Visible;
        ModuleTitleBarSeparator.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Empties the titlebar strip again. The buttons are not put back where they came from -
    /// the module they belong to is being torn down, so there is nothing to put them back
    /// into; dropping the reference is what lets both go.
    /// </summary>
    private void ReleaseModuleTitleBarStrip(ModuleOverlay overlay)
    {
        if (overlay.TitleBarStrip is null) return;

        ModuleTitleBarActions.Child = null;
        ModuleTitleBarActions.Visibility = Visibility.Collapsed;
        ModuleTitleBarSeparator.Visibility = Visibility.Collapsed;
    }

    /// <summary>Closes every open module, for the window shutting down underneath them.</summary>
    private void CloseAllModules()
    {
        foreach (var overlay in _openModules.ToList())
        {
            try { overlay.Close(); } catch { /* shutting down; a module's teardown is not worth a crash */ }
        }
    }
}
