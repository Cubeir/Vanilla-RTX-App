using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using static Vanilla_RTX_App.Core.EnvironmentVariables;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// What a feature module is instead of a <see cref="Window"/>: a control that covers
/// MainWindow's body from 36px down, fading in and out the way <see cref="MarkdownOverlay"/>
/// does.
///
/// <para><b>Its lifecycle is a window's lifecycle, deliberately.</b> Opening constructs one and
/// puts it in the tree; closing tears it down and takes it out again. The alternative - keeping
/// every module alive and merely hidden - was considered and is the wrong trade here: a hidden
/// module still holds its control locks, so the settings panel a module disables would stay
/// disabled for the rest of the session, and six resident modules means six live WebView2s,
/// timers and decoded preset-icon sets against the graphics budget §6 describes. Background work
/// a module started still outlives it, exactly as it did when these were windows.</para>
///
/// <para><b>Everything a module used to get from <see cref="Window"/> is here under the same
/// name</b>, so the hundreds of call sites inside them did not have to change:
/// <see cref="Close"/> for <c>this.Close()</c>, <see cref="Closed"/> for the host's own handler,
/// <see cref="WindowHandle"/> for <c>WindowNative.GetWindowHandle(this)</c>. What is *not* here
/// is anything a control genuinely cannot have - an AppWindow, a system backdrop, a titlebar of
/// its own - and those are the places each module actually changed.</para>
///
/// <para><b>It is concrete rather than abstract only because it is a XAML root element.</b>
/// Each module's markup declares <c>&lt;overlays:ModuleOverlay x:Class="..."&gt;</c>, which is
/// how the generated partial ends up deriving from this - and the markup compiler wants a type
/// it could construct. Nothing ever constructs one directly.</para>
///
/// <para><b>Both results are read after <see cref="Closed"/>, not during.</b>
/// <see cref="OperationSuccessful"/> and <see cref="StatusMessage"/> are the same contract every
/// one of these had as a window (§2), and MainWindow logs them from its close handler.</para>
/// </summary>
public class ModuleOverlay : UserControl
{
    /// <summary>Matches <see cref="MarkdownOverlay"/>'s fade, so switching surfaces feels like one app rather than several.</summary>
    private const double FADE_MS = 100;

    private static bool AnimationsSuspended => Persistent.SuspendUIAnimations;

    private Storyboard? _fadeStoryboard;
    private ModuleOverlayChrome? _chrome;
    private bool _closed;

    /// <summary>True between <see cref="Show"/> and <see cref="Close"/>. False again the instant a close starts, before the fade has run.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Whether this module did something worth reporting. Read by MainWindow after <see cref="Closed"/>.</summary>
    public bool OperationSuccessful { get; set; }

    /// <summary>What to log about it. An empty string means "say nothing", whatever <see cref="OperationSuccessful"/> holds.</summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>Raised once, after <see cref="OnClosing"/> and before the fade. The stand-in for <c>Window.Closed</c>.</summary>
    public event EventHandler? Closed;

    /// <summary>
    /// The window every module now lives inside, and what a module reaches the titlebar lamp
    /// through - <see cref="MainWindow.BlinkingLamp"/>, called with the arguments the event
    /// wants, the same way MainWindow and the settings panel call it. Modules could not touch
    /// it as windows, because a window covered it; it is on screen for all of them now, and it
    /// is the only feedback a module has that survives being closed a second later.
    /// </summary>
    protected static MainWindow Host => MainWindow.Instance!;

    /// <summary>
    /// MainWindow's HWND, for the file and folder pickers that need an owner. A module no
    /// longer has one of its own, and the pickers are modal to the app either way.
    /// </summary>
    protected static IntPtr WindowHandle => WindowNative.GetWindowHandle(MainWindow.Instance);

    /// <summary>
    /// A module's own titlebar buttons, if it has any. It is declared in the module's own XAML
    /// so its <c>x:Name</c> fields and <c>Click</c> handlers belong to that class, but it never
    /// renders there: <see cref="AttachChrome"/> lifts it straight back out, and MainWindow
    /// hosts it in its titlebar for as long as this module is open. Null - the usual case -
    /// means the module contributes nothing to the titlebar.
    /// </summary>
    protected internal virtual FrameworkElement? TitleBarStrip => null;

    /// <summary>
    /// Wraps the module's markup in the shared acrylic frame and close button. Every module
    /// calls this once, immediately after <c>InitializeComponent</c>: the frame cannot be
    /// applied by this base class's constructor because the derived XAML has not set
    /// <see cref="UserControl.Content"/> yet at that point.
    /// </summary>
    protected void AttachChrome()
    {
        if (_chrome is not null) return;

        var body = Content as UIElement;
        Content = null;

        _chrome = new ModuleOverlayChrome { Body = body };
        Content = _chrome;

        // Out of the module's own markup here, while nothing is in a live tree yet. Doing it
        // at open time instead is too late: the overlay is in the window by then, and pulling a
        // descendant out of a control that is mid-load does not take effect before the Border
        // it is handed to rejects it.
        if (TitleBarStrip is { } strip) Detach(body, strip);

        Visibility = Visibility.Collapsed;
        Opacity = 0;
        IsHitTestVisible = false;
    }

    /// <summary>Fades the overlay in. Safe to call once; a second call while open does nothing.</summary>
    public void Show()
    {
        if (IsOpen || _closed) return;
        IsOpen = true;

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        AnimateOpacity(1.0, null);
    }

    /// <summary>
    /// Tears the module down and takes it out of the window - the stand-in for
    /// <c>Window.Close()</c>, and idempotent for the same reason that was: several paths inside
    /// a module reach for it and a second call must not run the teardown twice.
    ///
    /// <para><b>Ordering is the contract.</b> <see cref="OnClosing"/> first, so a module has
    /// stopped its timers and written its result before anyone reads it; then
    /// <see cref="Closed"/>, which is where MainWindow logs that result and releases the
    /// controls this module locked; then the fade, then removal from the tree. Releasing the
    /// controls under a panel that is still visibly fading is harmless - nothing beneath it can
    /// be clicked for those last few frames anyway - and delaying it until the fade ended would
    /// mean a module that closes itself leaves the window dead for longer than it looks.</para>
    /// </summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        IsOpen = false;

        try { OnClosing(); }
        finally
        {
            Closed?.Invoke(this, EventArgs.Empty);

            IsHitTestVisible = false;
            AnimateOpacity(0.0, () =>
            {
                Visibility = Visibility.Collapsed;
                (Parent as Panel)?.Children.Remove(this);
            });
        }
    }

    /// <summary>
    /// The module's own teardown. Stop timers, cancel work, unsubscribe from anything
    /// static, and set
    /// <see cref="OperationSuccessful"/> / <see cref="StatusMessage"/>. Called exactly once.
    /// </summary>
    protected virtual void OnClosing() { }

    /// <summary>
    /// Takes <paramref name="target"/> out of whatever inside <paramref name="root"/> holds it,
    /// so it can be put somewhere else. An element may only be in one tree, and every API that
    /// accepts one rejects a still-held element with a bare "value does not fall within the
    /// expected range".
    ///
    /// <para><b>It searches rather than reading <c>Parent</c>, and that is the whole point.</b>
    /// An element that XAML has parsed but not yet realised reports a null <c>Parent</c> and a
    /// null visual parent while its container's <c>Children</c> still holds it - so trusting
    /// either one here finds nothing to detach, and the handover fails on an element that looks
    /// unparented from every angle except the one that matters.</para>
    /// </summary>
    private static bool Detach(object? root, FrameworkElement target)
    {
        switch (root)
        {
            case Panel panel:
                if (panel.Children.Remove(target)) return true;
                foreach (var child in panel.Children)
                    if (Detach(child, target)) return true;
                return false;

            case Border border:
                if (ReferenceEquals(border.Child, target)) { border.Child = null; return true; }
                return Detach(border.Child, target);

            // ScrollViewer and every other content host arrives here - they all derive from
            // ContentControl, and Content is the only thing that could be holding the target.
            case ContentControl holder:
                if (ReferenceEquals(holder.Content, target)) { holder.Content = null; return true; }
                return Detach(holder.Content, target);

            case ContentPresenter presenter:
                if (ReferenceEquals(presenter.Content, target)) { presenter.Content = null; return true; }
                return Detach(presenter.Content, target);

            default:
                return false;
        }
    }

    private void AnimateOpacity(double to, Action? onCompleted)
    {
        _fadeStoryboard?.Stop();
        _fadeStoryboard = null;

        if (AnimationsSuspended)
        {
            Opacity = to;
            onCompleted?.Invoke();
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(FADE_MS)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, this);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            // The end value written back as the base value: a stopped storyboard reverts its
            // target, and Stop is the first thing the next fade does.
            Opacity = to;
            onCompleted?.Invoke();
        };

        _fadeStoryboard = storyboard;
        storyboard.Begin();
    }
}
