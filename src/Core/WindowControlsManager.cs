using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vanilla_RTX_App.Modules;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Disables and restores interactive controls across a window while a long operation runs,
/// without any caller having to know what state those controls were in beforehand.
///
/// <para><b>Locks are reference-counted, keyed by control instance.</b> Several feature
/// windows can be open at once and each disables what it cares about; a control's IsEnabled
/// is only restored once the last of them has released it. Without the count, whichever
/// window finished first would re-enable buttons another one still needs held down. Keyed on
/// the instance rather than the name because Control doesn't override Equals/GetHashCode, so
/// identity is exactly the right comparison and two same-named controls in different windows
/// stay distinct.</para>
///
/// <para><b>Call Acquire and Release in pairs</b> - every <c>Toggle*(window, false)</c> needs
/// its <c>true</c> counterpart, normally from the window's Closed handler. A lock that is
/// never released leaves the control disabled for the rest of the session;
/// <see cref="ClearStates"/> is the way out of that.</para>
/// </summary>
public class WindowControlsManager
{
    /// <summary>
    /// Controls that stay live no matter what: the two document buttons, the lamp, the log and
    /// its progress bar. None of them can affect an operation in flight, and locking the user
    /// out of help or the log during a long run is the opposite of useful.
    ///
    /// <para><b>SettingsButton is deliberately NOT here.</b> The settings panel is where the
    /// Minecraft install and user data locations are changed, and every feature that locks this
    /// window down is using those locations while it runs - a path swapped mid-operation leaves
    /// that operation writing to one folder and the app pointed at another. It is excluded from
    /// nothing, so <see cref="MainWindow.LockControls"/> can take it away for the duration.</para>
    /// </summary>
    /// <remarks>
    /// Exclusions are matched by name, and every feature module is now a control inside
    /// MainWindow rather than a window of its own - so a blanket or targeted toggle started
    /// from MainWindow walks into an open module's subtree too. Nothing collides today
    /// (checked name by name), and a module naming a control after one of MainWindow's would
    /// find itself disabled alongside it.
    /// </remarks>
    private static readonly HashSet<string> _globalExclusions = new()
    {
        "HelpButton", "BugButton",
        "LampInteractionButton", "SidebarLog", "SidelogProgressBar",
    };

    // Reference-counted lock state, keyed by control INSTANCE (identity-based dictionary —
    // Control doesn't override Equals/GetHashCode, so this is safe). A control's IsEnabled is
    // restored only once its lock count returns to zero, so overlapping disable sessions
    // (multiple feature windows open at once) can no longer stomp on each other.
    private static readonly Dictionary<Control, int> _lockCounts = new();
    private static readonly Dictionary<Control, bool> _preLockState = new();

    /// <summary>
    /// Blanket mode: disable every supported control in the window EXCEPT the named ones
    /// (plus global exclusions). Use for "lock the whole window down" operations.
    /// </summary>
    public static void ToggleControls(Window window, bool enable, bool overrideGlobalExclusions = false, params string[] excludeNames)
    {
        if (window == null) return;
        var content = TryGetContent(window);
        if (content == null) return;

        ToggleControls(content, enable, overrideGlobalExclusions, excludeNames);
    }

    /// <summary>
    /// Targeted mode: disable/enable ONLY the named controls (global exclusions still apply
    /// as a safety net). Use for feature windows that should block a specific handful of
    /// buttons rather than the whole UI.
    /// </summary>
    public static void ToggleSpecificControls(Window window, bool enable, params string[] controlNames)
        => ToggleSpecificControls(TryGetContent(window), enable, controlNames);

    /// <summary>
    /// <see cref="ToggleSpecificControls(Window, bool, string[])"/> scoped to one subtree
    /// rather than a whole window. A feature module is a control inside MainWindow now, so
    /// passing itself here reaches its own controls and nothing else - where walking from the
    /// window would also sweep MainWindow's, and every other overlay's.
    /// </summary>
    public static void ToggleSpecificControls(UIElement? root, bool enable, params string[] controlNames)
    {
        if (root == null || controlNames == null || controlNames.Length == 0) return;

        var wanted = new HashSet<string>(controlNames);
        wanted.ExceptWith(_globalExclusions);

        var controls = GetAllSupportedControls(root, null).Where(c => wanted.Contains(c.Name));
        Apply(enable, controls);
    }

    /// <summary>
    /// <see cref="ToggleControls"/> scoped to one subtree, for the same reason as the
    /// targeted overload above.
    /// </summary>
    public static void ToggleControls(UIElement? root, bool enable, bool overrideGlobalExclusions = false, params string[] excludeNames)
    {
        if (root == null) return;

        var exclusions = overrideGlobalExclusions ? new HashSet<string>() : new HashSet<string>(_globalExclusions);
        if (excludeNames != null)
            foreach (var name in excludeNames)
                if (!string.IsNullOrEmpty(name)) exclusions.Add(name);

        Apply(enable, GetAllSupportedControls(root, exclusions));
    }

    /// <summary>
    /// Force-releases every lock held on anything inside <paramref name="root"/>, whatever its
    /// count. The escape hatch for a subtree leaving the visual tree with locks outstanding -
    /// a control that is gone can still be holding a count, and that count is what would stop
    /// the next real lock on a same-named control from ever reaching zero.
    /// </summary>
    public static void ClearStates(UIElement? root)
    {
        if (root == null) return;

        foreach (var control in GetAllSupportedControls(root, null).ToList())
        {
            if (_lockCounts.Remove(control) && _preLockState.Remove(control, out var original))
                control.IsEnabled = original;
        }
    }
    // Window.Content throws COMException instead of returning null once the native window
    // has been torn down (e.g. MainWindow closed while a child feature window is still open
    // and later calls back into it via a captured "this"). Swallow that specific case —
    // there's nothing left to toggle on a dead window — but let anything else bubble up.
    /// <summary>
    /// <see cref="Window.Content"/>, or null if the window's native side is already gone.
    ///
    /// <para>Window.Content throws COMException rather than returning null once the HWND has
    /// been torn down, which a child feature window hits whenever it calls back into a
    /// MainWindow that closed underneath it through a captured reference. Only that case is
    /// swallowed - there is nothing left to toggle on a dead window - and anything else
    /// bubbles.</para>
    /// </summary>
    private static UIElement? TryGetContent(Window window)
    {
        try
        {
            return window.Content;
        }
        catch (COMException)
        {
            return null;
        }
    }



    /// <summary>
    /// Emergency reset - force-clears every lock on every control in the window regardless of
    /// count. Not part of normal flow; use only if a window can be torn down without its
    /// Closed handler running (crash, forced termination, etc).
    /// </summary>
    public static void ClearStates(Window window) => ClearStates(TryGetContent(window));

    /// <summary>
    /// Routes to <see cref="Acquire"/> or <see cref="Release"/>, materialising the sequence
    /// first: <see cref="GetAllSupportedControls"/> walks the live visual tree lazily, and
    /// enumerating it while changing IsEnabled on what it yields is a mutation mid-walk.
    /// </summary>
    private static void Apply(bool enable, IEnumerable<Control> controls)
    {
        var list = controls as IList<Control> ?? controls.ToList();
        if (enable) Release(list); else Acquire(list);
    }

    /// <summary>
    /// Takes a lock on each control, recording its pre-lock IsEnabled the first time only -
    /// on a second acquire the control is already disabled by us, and recording that would
    /// make the eventual restore re-disable it.
    /// </summary>
    private static void Acquire(IEnumerable<Control> controls)
    {
        foreach (var control in controls)
        {
            _lockCounts.TryGetValue(control, out var count);
            if (count == 0)
            {
                _preLockState[control] = control.IsEnabled;
                control.IsEnabled = false;
            }
            _lockCounts[control] = count + 1;
        }
    }

    /// <summary>
    /// Drops one lock per control and restores the recorded state at zero. A release for a
    /// control that was never locked is ignored rather than counted negative, so an unpaired
    /// restore cannot leave the next real lock unable to reach zero.
    /// </summary>
    private static void Release(IEnumerable<Control> controls)
    {
        foreach (var control in controls)
        {
            if (!_lockCounts.TryGetValue(control, out var count) || count <= 0)
                continue; // never locked / already released — ignore stray release, don't go negative

            count--;
            if (count == 0)
            {
                _lockCounts.Remove(control);
                if (_preLockState.Remove(control, out var original))
                    control.IsEnabled = original;
            }
            else
            {
                _lockCounts[control] = count;
            }
        }
    }

    // ── Remote suspension ─────────────────────────────────────────────────────

    private static readonly HashSet<string> _suspendedNames = new(StringComparer.OrdinalIgnoreCase);
    // Weak, because a module's controls leave the tree when it closes and must not be kept
    // alive by having once been suspended.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _suspendedControls = new();

    /// <summary>
    /// Disables every control under <paramref name="root"/> whose name is in
    /// <paramref name="names"/>, and keeps it disabled for the rest of the session. This is the
    /// remote kill switch driven by the announcements .md's <c># SuspendControls</c> section
    /// (<see cref="OnlineTextsContent.SuspendControls"/>).
    ///
    /// <para><b>Additive and idempotent.</b> Names accumulate across calls, and a control
    /// already suspended is left alone, so calling this once from cache and again after a fresh
    /// fetch is safe. Nothing is ever un-suspended: a name dropped from the .md takes effect on
    /// the next launch.</para>
    ///
    /// <para><b>A suspended control is held down by its own <c>IsEnabledChanged</c>, not by a
    /// lock count.</b> Every path that re-enables a control - a <see cref="Release"/> restoring
    /// its pre-lock state, <see cref="ClearStates(UIElement?)"/>, a module assigning
    /// <c>IsEnabled = true</c> outright - is immediately undone. A lock count could only stop
    /// the first of those.</para>
    ///
    /// <para>Only controls in the visual tree at the time can be found, so a feature module's
    /// controls are reached by <see cref="ApplySuspensions"/> as each module loads, and a
    /// <c>MenuFlyoutItem</c> inside a flyout that has never opened is out of reach.</para>
    /// </summary>
    public static void SuspendControls(UIElement? root, IEnumerable<string>? names)
    {
        if (names != null)
            foreach (var name in names)
                if (!string.IsNullOrWhiteSpace(name)) _suspendedNames.Add(name.Trim());

        ApplySuspensions(root);
    }

    /// <summary>
    /// Suspends whatever under <paramref name="root"/> matches a name already passed to
    /// <see cref="SuspendControls"/>. Free when nothing is suspended, which is every session
    /// where the .md doesn't ask for it.
    /// </summary>
    public static void ApplySuspensions(UIElement? root)
    {
        if (root == null || _suspendedNames.Count == 0) return;

        foreach (var control in GetAllSupportedControls(root, null).ToList())
        {
            if (!_suspendedNames.Contains(control.Name) || !_suspendedControls.TryAdd(control, _suspendedNames)) continue;

            control.IsEnabled = false;
            control.IsEnabledChanged += KeepSuspended;
            System.Diagnostics.Trace.WriteLine($"[WindowControlsManager] Suspended '{control.Name}'");
        }
    }

    private static void KeepSuspended(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Control { IsEnabled: true } control)
            control.IsEnabled = false;
    }

    /// <summary>Adds a control name to <see cref="_globalExclusions"/> for the rest of the session.</summary>
    public static void AddGlobalExclusion(string controlName) { if (!string.IsNullOrEmpty(controlName)) _globalExclusions.Add(controlName); }

    /// <summary>Stops sparing that control name. It does not release a lock already held on it.</summary>
    public static void RemoveGlobalExclusion(string controlName) { if (!string.IsNullOrEmpty(controlName)) _globalExclusions.Remove(controlName); }

    /// <summary>Empties the exclusion set, after which a blanket toggle really does reach everything.</summary>
    public static void ClearGlobalExclusions() => _globalExclusions.Clear();

    /// <summary>
    /// Every <see cref="IsSupportedControl"/> descendant of <paramref name="parent"/>, depth
    /// first, skipping names in <paramref name="exclusions"/> (null excludes nothing).
    ///
    /// <para>Walks the visual tree rather than a registry, so controls created at runtime are
    /// found without anyone registering them - which is what lets the preset and version
    /// lists, rebuilt wholesale on every redraw, participate at all. An excluded parent does
    /// not exclude its children; exclusion is per control.</para>
    /// </summary>
    private static IEnumerable<Control> GetAllSupportedControls(DependencyObject parent, HashSet<string>? exclusions)
    {
        if (parent == null) yield break;

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);

            if (IsSupportedControl(child))
            {
                var control = (Control)child;
                if (exclusions == null || !exclusions.Contains(control.Name))
                    yield return control;
            }

            foreach (var grandChild in GetAllSupportedControls(child, exclusions))
                yield return grandChild;
        }
    }

    /// <summary>
    /// Whether this is something with an IsEnabled worth toggling. Deliberately a list of
    /// input controls rather than "anything deriving from Control": disabling a container,
    /// a TextBlock or a ContentPresenter greys its subtree without stopping anything, and
    /// makes the restore ambiguous when a child was independently disabled.
    /// </summary>
    private static bool IsSupportedControl(DependencyObject control) =>
        control is Button or CheckBox or RadioButton or Slider or TextBox or PasswordBox or ComboBox or
        ListBox or ListView or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton or RatingControl or
        NumberBox or DatePicker or TimePicker or ToggleSwitch or MenuFlyoutItem or AppBarButton or
        AppBarToggleButton or AutoSuggestBox;

}

/// <summary>
/// Window-typed shorthand for <see cref="WindowControlsManager"/>'s blanket mode. Every
/// disable still needs its matching enable - these are sugar, not scope guards.
/// </summary>
public static class WindowControlsManagerExtensions
{
    /// <summary>Takes a lock on every supported control in the window.</summary>
    /// <param name="overrideGlobalExclusions">True also locks help/donate/theme/log.</param>
    /// <param name="excludeNames">Names to leave alone on top of the global exclusions.</param>
    public static void DisableAllControls(this Window window, bool overrideGlobalExclusions = false, params string[] excludeNames)
    {
        WindowControlsManager.ToggleControls(window, false, overrideGlobalExclusions, excludeNames);
    }

    /// <summary>
    /// Releases the lock taken by <see cref="DisableAllControls"/>. Pass the same arguments
    /// it was given, or the sets won't match and some controls keep a lock nothing releases.
    /// </summary>
    public static void EnableAllControls(this Window window, bool overrideGlobalExclusions = false, params string[] excludeNames)
    {
        WindowControlsManager.ToggleControls(window, true, overrideGlobalExclusions, excludeNames);
    }

    /// <summary>
    /// Releases one lock on every supported control except the global exclusions - the
    /// counterpart to a plain <c>DisableAllControls()</c> with no arguments.
    /// </summary>
    public static void RestoreAllControls(this Window window)
    {
        WindowControlsManager.ToggleControls(window, true);
    }

    /// <summary>Emergency reset - see <see cref="WindowControlsManager.ClearStates"/>.</summary>
    public static void ClearControlStates(this Window window)
    {
        WindowControlsManager.ClearStates(window);
    }
}

/// <summary>
/// Utility class for driving a WinUI ProgressBar safely from background threads,
/// with three modes:
///   - Indeterminate "something is happening" (original ShowProgress/HideProgress behavior,
///     kept for any other long-running operation in the app that doesn't report real progress).
///   - Determinate, fed by Tuner's IProgress&lt;TuningProgress&gt; reports (falls back to
///     indeterminate automatically while the total unit count is still unknown).
///   - Error / cancelled terminal states, using the ProgressBar's built-in ShowError/ShowPaused
///     visuals so the user gets a distinct look without extra UI.
/// All public methods are safe to call from any thread; UI mutation is always marshalled
/// onto the ProgressBar's DispatcherQueue.
/// </summary>
public class ProgressBarManager
{
    private readonly ProgressBar _progressBar;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _lock = new();
    private int _activeOperations;

    /// <summary>
    /// Binds to one ProgressBar and captures its DispatcherQueue, which is what makes every
    /// method here callable from any thread. The bar is reset to hidden immediately, so a
    /// manager constructed against a bar left visible in XAML starts from a known state.
    /// </summary>
    public ProgressBarManager(ProgressBar progressBar)
    {
        _progressBar = progressBar ?? throw new ArgumentNullException(nameof(progressBar));
        _dispatcherQueue = progressBar.DispatcherQueue;
        ResetVisual();
    }

    // ── Legacy indeterminate public API ─────────────────────────

    /// <summary>
    /// Shows the progress bar in indeterminate mode. Call this when starting a long-running
    /// operation that has no meaningful "percent done". Multiple calls are safe - the bar
    /// stays visible until all operations complete.
    /// </summary>
    public void ShowProgress()
    {
        lock (_lock)
        {
            _activeOperations++;
            UpdateIndeterminateState();
        }
    }

    /// <summary>
    /// Hides the progress bar. Only hides once every ShowProgress() call has a matching
    /// HideProgress() call.
    /// </summary>
    public void HideProgress()
    {
        lock (_lock)
        {
            if (_activeOperations > 0)
            {
                _activeOperations--;
                UpdateIndeterminateState();
            }
        }
    }

    /// <summary>Forces the progress bar to hide regardless of active operation count.</summary>
    public void ForceHide()
    {
        lock (_lock)
        {
            _activeOperations = 0;
            UpdateIndeterminateState();
        }
    }

    /// <summary>Whether any indeterminate operation is still outstanding.</summary>
    public bool IsVisible
    {
        get { lock (_lock) { return _activeOperations > 0; } }
    }

    /// <summary>
    /// How many unmatched <see cref="ShowProgress"/> calls are outstanding. Diagnostic: a
    /// count that never returns to zero means a caller lost its HideProgress to an early
    /// return or an exception.
    /// </summary>
    public int ActiveOperationsCount
    {
        get { lock (_lock) { return _activeOperations; } }
    }

    /// <summary>
    /// Shows or hides the bar from the operation count, clearing the error and paused tints
    /// on the way - those are terminal states, and a new operation starting means they no
    /// longer describe anything. Called under <c>_lock</c>.
    /// </summary>
    private void UpdateIndeterminateState()
    {
        var shouldShow = _activeOperations > 0;
        RunOnUi(() =>
        {
            _progressBar.ShowError = false;
            _progressBar.ShowPaused = false;
            _progressBar.IsIndeterminate = shouldShow;
            _progressBar.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // ── New determinate API, meant for Tuner.TuneSelectedPacks' IProgress<T> ──

    /// <summary>
    /// Feeds a Tuner.TuningProgress report straight into the bar. Pass this as
    /// `new Progress&lt;Tuner.TuningProgress&gt;(progressManager.ReportTuningProgress)`.
    /// Total &lt;= 0 means the amount of work isn't known yet, so the bar stays
    /// indeterminate until a real total shows up.
    /// </summary>
    public void ReportTuningProgress(Tuner.TuningProgress p)
    {
        RunOnUi(() =>
        {
            _progressBar.ShowError = false;
            _progressBar.ShowPaused = false;
            _progressBar.Visibility = Visibility.Visible;

            if (p.Total <= 0)
            {
                _progressBar.IsIndeterminate = true;
                return;
            }

            _progressBar.IsIndeterminate = false;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = p.Total;
            _progressBar.Value = Math.Clamp(p.Completed, 0, p.Total);
        });
    }

    /// <summary>Marks the bar complete and hides it (success path).</summary>
    public void Complete()
    {
        RunOnUi(ResetVisual);
    }

    /// <summary>Shows the built-in WinUI "error" tint, e.g. after an exception.</summary>
    public void ReportError()
    {
        RunOnUi(() =>
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.ShowPaused = false;
            _progressBar.ShowError = true;
            _progressBar.Visibility = Visibility.Visible;
        });
    }

    /// <summary>Shows the built-in WinUI "paused" tint, used here for user-initiated cancellation.</summary>
    public void ReportCancelled()
    {
        RunOnUi(() =>
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.ShowError = false;
            _progressBar.ShowPaused = true;
            _progressBar.Visibility = Visibility.Visible;
        });
    }

    /// <summary>Back to hidden, determinate, untinted, value zero - the bar's resting state.</summary>
    private void ResetVisual()
    {
        _progressBar.IsIndeterminate = false;
        _progressBar.ShowError = false;
        _progressBar.ShowPaused = false;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Value = 0;
        _progressBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the bar's UI thread, inline when already there.
    /// The inline path matters for ordering: enqueuing unconditionally would let a UI-thread
    /// caller's own later statements run before the update it just asked for.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
            action();
        else
            _dispatcherQueue.TryEnqueue(() => action());
    }
}

public static class ProgressBarExtensions
{
    private static readonly Dictionary<ProgressBar, ProgressBarManager> _managers = new();

    /// <summary>
    /// Gets or creates a ProgressBarManager for this ProgressBar instance.
    /// </summary>
    public static ProgressBarManager GetManager(this ProgressBar progressBar)
    {
        if (!_managers.TryGetValue(progressBar, out var manager))
        {
            manager = new ProgressBarManager(progressBar);
            _managers[progressBar] = manager;
        }
        return manager;
    }

    /// <summary>
    /// Shows progress on this ProgressBar. Thread-safe and handles multiple concurrent operations.
    /// </summary>
    public static void ShowProgress(this ProgressBar progressBar)
    {
        progressBar.GetManager().ShowProgress();
    }

    /// <summary>
    /// Hides progress on this ProgressBar. Only hides when all operations complete.
    /// </summary>
    public static void HideProgress(this ProgressBar progressBar)
    {
        progressBar.GetManager().HideProgress();
    }

    /// <summary>
    /// Forces the ProgressBar to hide regardless of active operations.
    /// </summary>
    public static void ForceHideProgress(this ProgressBar progressBar)
    {
        progressBar.GetManager().ForceHide();
    }
}
