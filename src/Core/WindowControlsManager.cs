using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Universal control toggle utility for WinUI 3 applications
/// Manages enabling/disabling of controls while preserving original states
/// </summary>
public class WindowControlsManager
{
    private static readonly HashSet<string> _globalExclusions = new()
    {
        "HelpButton", "DonateButton", "ChatButton", "CycleThemeButton",
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

        var exclusions = overrideGlobalExclusions ? new HashSet<string>() : new HashSet<string>(_globalExclusions);
        if (excludeNames != null)
            foreach (var name in excludeNames)
                if (!string.IsNullOrEmpty(name)) exclusions.Add(name);

        Apply(enable, GetAllSupportedControls(content, exclusions));
    }

    /// <summary>
    /// Targeted mode: disable/enable ONLY the named controls (global exclusions still apply
    /// as a safety net). Use for feature windows that should block a specific handful of
    /// buttons rather than the whole UI.
    /// </summary>
    public static void ToggleSpecificControls(Window window, bool enable, params string[] controlNames)
    {
        if (window == null || controlNames == null || controlNames.Length == 0) return;
        var content = TryGetContent(window);
        if (content == null) return;

        var wanted = new HashSet<string>(controlNames);
        wanted.ExceptWith(_globalExclusions);

        var controls = GetAllSupportedControls(content, null).Where(c => wanted.Contains(c.Name));
        Apply(enable, controls);
    }
    // Window.Content throws COMException instead of returning null once the native window
    // has been torn down (e.g. MainWindow closed while a child feature window is still open
    // and later calls back into it via a captured "this"). Swallow that specific case —
    // there's nothing left to toggle on a dead window — but let anything else bubble up.
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
    /// Emergency reset — force-clears every lock on every control in the window regardless of
    /// count. Not part of normal flow; use only if a window can be torn down without its
    /// Closed handler running (crash, forced termination, etc).
    /// </summary>
    public static void ClearStates(Window window)
    {
        if (window?.Content == null) return;

        foreach (var control in GetAllSupportedControls(window.Content, null).ToList())
        {
            if (_lockCounts.Remove(control) && _preLockState.Remove(control, out var original))
                control.IsEnabled = original;
        }
    }

    private static void Apply(bool enable, IEnumerable<Control> controls)
    {
        var list = controls as IList<Control> ?? controls.ToList(); // materialize before mutating
        if (enable) Release(list); else Acquire(list);
    }

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

    public static void AddGlobalExclusion(string controlName) { if (!string.IsNullOrEmpty(controlName)) _globalExclusions.Add(controlName); }
    public static void RemoveGlobalExclusion(string controlName) { if (!string.IsNullOrEmpty(controlName)) _globalExclusions.Remove(controlName); }
    public static void ClearGlobalExclusions() => _globalExclusions.Clear();

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

    private static bool IsSupportedControl(DependencyObject control) =>
        control is Button or CheckBox or RadioButton or Slider or TextBox or PasswordBox or ComboBox or
        ListBox or ListView or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton or RatingControl or
        NumberBox or DatePicker or TimePicker or ToggleSwitch or MenuFlyoutItem or AppBarButton or
        AppBarToggleButton or AutoSuggestBox;
}

// TODO: you forgot you have this?! this would've been so useful to use in other windows wherever you needed to stop user from intracting with the window for a bit
// Not only that, these would've simplified otherwise convoluted uses of togglecontrols in some scenarios. Check.
/// <summary>
/// Extension methods for convenient usage
/// </summary>
public static class WindowControlsManagerExtensions
{
    /// <summary>
    /// Toggles all controls in this window
    /// </summary>
    /// <param name="window">The window to toggle controls for</param>
    /// <param name="enable">True to restore, false to disable</param>
    /// <param name="excludeNames">Optional list of control names to exclude</param>
    public static void DisableAllControls(this Window window, bool overrideGlobalExclusions = false, params string[] excludeNames)
    {
        WindowControlsManager.ToggleControls(window, false, overrideGlobalExclusions, excludeNames);
    }
    /// <summary>
    /// Disables all controls in this window
    /// </summary>
    /// <param name="window">The window to disable controls for</param>
    /// <param name="excludeNames">Optional list of control names to exclude</param>
    public static void EnableAllControls(this Window window, bool overrideGlobalExclusions = false, params string[] excludeNames)
    {
        WindowControlsManager.ToggleControls(window, true, overrideGlobalExclusions, excludeNames);
    }

    /// <summary>
    /// Restores all controls in this window to their original states
    /// </summary>
    /// <param name="window">The window to restore controls for</param>
    public static void RestoreAllControls(this Window window)
    {
        WindowControlsManager.ToggleControls(window, true);
    }

    /// <summary>
    /// Clears stored control states for this window
    /// </summary>
    /// <param name="window">The window to clear states for</param>
    public static void ClearControlStates(this Window window)
    {
        WindowControlsManager.ClearStates(window);
    }
}
// TODO: Just marking it, come here and check this out later, expand on it, non-intermidate states, paused, and error states, support them
// begin properly utilizing it
/// <summary>
/// This shouldn't really be here, a utility class for managing a simple progress bar on/off while preventing race conditions
/// Update it later to use a more robust solution like IProgress<T> or async/await patterns, show real time progress, etc.
/// In fact, remove the thing entirely, you've already got the lamp to show "something is going on" this is just VISUAL CLUTTER
/// </summary>
public class ProgressBarManager
{
    private readonly ProgressBar _progressBar;
    private int _activeOperations = 0;
    private readonly object _lock = new();

    public ProgressBarManager(ProgressBar progressBar)
    {
        _progressBar = progressBar ?? throw new ArgumentNullException(nameof(progressBar));

        // Initialize to hidden state
        _progressBar.IsIndeterminate = false;
        _progressBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the progress bar. Call this when starting a long-running operation.
    /// Multiple calls are safe - progress bar stays visible until all operations complete.
    /// </summary>
    public void ShowProgress()
    {
        lock (_lock)
        {
            _activeOperations++;
            UpdateProgressBarState();
        }
    }

    /// <summary>
    /// Hides the progress bar. Call this when completing a long-running operation.
    /// Progress bar only hides when all operations have completed.
    /// </summary>
    public void HideProgress()
    {
        lock (_lock)
        {
            if (_activeOperations > 0)
            {
                _activeOperations--;
                UpdateProgressBarState();
            }
        }
    }

    /// <summary>
    /// Forces the progress bar to hide regardless of active operations count.
    /// Use sparingly, typically only for error handling or app shutdown.
    /// </summary>
    public void ForceHide()
    {
        lock (_lock)
        {
            _activeOperations = 0;
            UpdateProgressBarState();
        }
    }

    /// <summary>
    /// Gets whether the progress bar is currently visible.
    /// </summary>
    public bool IsVisible
    {
        get
        {
            lock (_lock)
            {
                return _activeOperations > 0;
            }
        }
    }

    /// <summary>
    /// Gets the current number of active operations.
    /// </summary>
    public int ActiveOperationsCount
    {
        get
        {
            lock (_lock)
            {
                return _activeOperations;
            }
        }
    }

    private void UpdateProgressBarState()
    {
        var shouldShow = _activeOperations > 0;

        _progressBar.IsIndeterminate = shouldShow;
        _progressBar.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
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
