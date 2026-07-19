using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using System.Diagnostics;

namespace Vanilla_RTX_App.Core;

// TODO: Animate Opacity, instead of suddenly making it visible?
public sealed partial class ReviewPromptControl : UserControl
{
    public event EventHandler? Closed;

    public ReviewPromptControl()
    {
        this.InitializeComponent();

        ReviewButton.Click += ReviewButton_Click;
        LaterButton.Click += LaterButton_Click;
        NeverButton.Click += NeverButton_Click;
        // Make sure the control takes full size of parent
        this.HorizontalAlignment = HorizontalAlignment.Stretch;
        this.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void RootGrid_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Clicking the backdrop (outside dialog) = "Show later"
        ReviewPromptManager.ResetTimer();
        Hide();
    }

    private void DialogBorder_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // prevent taps on the dialog itself from closing it
        e.Handled = true;
    }

    private async void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-windows-store://review/?ProductId=9N6PCRZ5V9DJ"));
        ReviewPromptManager.MarkAsCompleted();
        Hide();
    }

    private async void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        ReviewPromptManager.ResetTimer();
        Hide();
    }

    private async void NeverButton_Click(object sender, RoutedEventArgs e)
    {
        ReviewPromptManager.NeverShowAgain();
        Hide();
    }

    public void Show()
    {
        RootGrid.Visibility = Visibility.Visible;
        Trace.WriteLine("RootGrid visibility set to Visible");
    }

    public void Hide()
    {
        RootGrid.Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}

public static class ReviewPromptManager
{
    private static readonly string FIRST_LAUNCH_KEY = "ReviewPromptFirstLaunchTime";
    private static readonly string DONT_SHOW_KEY = $"ReviewPromptDontShow_{TunerVariables.appVersionMajorMinor}"; // Ask again only with Major or Minor updates (not new builds/revisions)

    private static readonly string LAST_PROMPT_KEY = "ReviewPromptLastPromptTime";
    private const double MINUTES_BEFORE_PROMPT = 4444; // how many hours to wait before showing for the first time, or showing again if deferred
    private const int SHOW_DELAY_Milisecs = 0; // delay to show it after being called

    private static void CleanupOldVersionKeys()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            var keysToRemove = localSettings.Values.Keys
                .Where(k => k.StartsWith("ReviewPromptDontShow_") && k != DONT_SHOW_KEY)
                .ToList();
            foreach (var key in keysToRemove)
                localSettings.Values.Remove(key);
        }
        catch
        {
            Trace.WriteLine("[ReviewPrompt] Failed to clear orhphaned ReviewPromptDontShow_ keys");
        }
    }

    private static ReviewPromptControl? _currentPrompt;
    private static Panel? _rootPanel;

    /// <summary>
    /// Initialize and show the review prompt if conditions are met.
    /// Call this once on app startup.
    /// </summary>
    /// <param name="rootPanel">The root panel of your MainWindow (e.g., the main Grid)</param>
    public static async Task InitializeAsync(Panel rootPanel)
    {
        Trace.WriteLine("=== ReviewPrompt: InitializeAsync called ===");
        _rootPanel = rootPanel;

        if (_rootPanel == null)
        {
            Trace.WriteLine("ERROR: rootPanel is NULL!");
            return;
        }

        Trace.WriteLine($"Root panel type: {_rootPanel.GetType().Name}");

        CleanupOldVersionKeys();
        // Record first launch if not already recorded
        await RecordFirstLaunchIfNeededAsync();

        // Check if we should show the prompt
        bool shouldShow = await ShouldShowPromptAsync();
        Trace.WriteLine($"Should show prompt: {shouldShow}");

        if (shouldShow)
        {
            Trace.WriteLine($"Waiting {SHOW_DELAY_Milisecs} seconds before showing...");
            await Task.Delay(TimeSpan.FromMilliseconds(SHOW_DELAY_Milisecs));
            Trace.WriteLine("Calling ShowPrompt()...");
            ShowPrompt();
        }
    }

    private static async Task RecordFirstLaunchIfNeededAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;

        if (!localSettings.Values.ContainsKey(FIRST_LAUNCH_KEY))
        {
            var now = DateTime.UtcNow.Ticks;
            localSettings.Values[FIRST_LAUNCH_KEY] = now;
            Trace.WriteLine($"First launch recorded: {now} ticks ({DateTime.UtcNow})");
        }
        else
        {
            Trace.WriteLine($"First launch already recorded: {localSettings.Values[FIRST_LAUNCH_KEY]} ticks");
        }
    }

    private static async Task<bool> ShouldShowPromptAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;

        // Check if user said "Don't show again"
        if (localSettings.Values.ContainsKey(DONT_SHOW_KEY))
        {
            Trace.WriteLine("Don't show key exists - returning false");
            return false;
        }

        // Get first launch time
        if (!localSettings.Values.ContainsKey(FIRST_LAUNCH_KEY))
        {
            Trace.WriteLine("No first launch key - returning false");
            return false;
        }

        var firstLaunchTicks = localSettings.Values[FIRST_LAUNCH_KEY];
        if (firstLaunchTicks == null || !(firstLaunchTicks is long))
        {
            Trace.WriteLine($"Invalid first launch ticks: {firstLaunchTicks}");
            return false;
        }

        var firstLaunch = new DateTime((long)firstLaunchTicks, DateTimeKind.Utc);
        Trace.WriteLine($"First launch: {firstLaunch} UTC");

        // Check if time has passed since first launch (or last "Show later")
        DateTime checkTime = firstLaunch;

        if (localSettings.Values.ContainsKey(LAST_PROMPT_KEY))
        {
            var lastPromptTicks = localSettings.Values[LAST_PROMPT_KEY];
            if (lastPromptTicks is long)
            {
                var lastPrompt = new DateTime((long)lastPromptTicks, DateTimeKind.Utc);
                checkTime = lastPrompt;
                Trace.WriteLine($"Using last prompt time: {lastPrompt} UTC");
            }
        }

        var minutesSince = (DateTime.UtcNow - checkTime).TotalMinutes;
        Trace.WriteLine($"Minutes since check time: {minutesSince} (need {MINUTES_BEFORE_PROMPT})");
        Trace.WriteLine($"Current UTC: {DateTime.UtcNow}");

        return minutesSince >= MINUTES_BEFORE_PROMPT;
    }

    private static void ShowPrompt()
    {
        Trace.WriteLine("=== ShowPrompt() called ===");

        if (_rootPanel == null)
        {
            Trace.WriteLine("ERROR: _rootPanel is NULL in ShowPrompt!");
            return;
        }

        if (_currentPrompt != null)
        {
            Trace.WriteLine("Prompt already showing!");
            return;
        }

        Trace.WriteLine("Creating new ReviewPromptControl...");
        _currentPrompt = new ReviewPromptControl();

        // ensure it appears on top
        Canvas.SetZIndex(_currentPrompt, 9999);

        // span all columns and rows beucase your mainwindow has 2 sections
        if (_rootPanel is Grid)
        {
            Grid.SetColumnSpan(_currentPrompt, int.MaxValue);
            Grid.SetRowSpan(_currentPrompt, int.MaxValue);
            Trace.WriteLine("Set ColumnSpan and RowSpan to cover entire Grid");
        }

        _currentPrompt.Closed += (s, e) =>
        {
            Trace.WriteLine("Prompt closed event fired");
            if (_rootPanel.Children.Contains(_currentPrompt))
            {
                _rootPanel.Children.Remove(_currentPrompt);
            }
            _currentPrompt = null;
        };

        Trace.WriteLine("Adding prompt to root panel...");
        _rootPanel.Children.Add(_currentPrompt);

        Trace.WriteLine($"Current children count: {_rootPanel.Children.Count}");

        Trace.WriteLine("Calling Show() on prompt...");
        _currentPrompt.Show();

        Trace.WriteLine("=== ShowPrompt() complete ===");
    }

    internal static void ResetTimer()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[LAST_PROMPT_KEY] = DateTime.UtcNow.Ticks;
        Trace.WriteLine("Timer reset - will show again after delay");
    }

    internal static void NeverShowAgain()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[DONT_SHOW_KEY] = true;
        Trace.WriteLine("Never show again flag set");
    }

    internal static void MarkAsCompleted()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[DONT_SHOW_KEY] = true;
        Trace.WriteLine("Review completed - will not show again");
    }

    /// <summary>
    /// Debug method to clear all settings and force the prompt to show on next launch
    /// </summary>
    public static void ResetForTesting()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values.Remove(FIRST_LAUNCH_KEY);
        localSettings.Values.Remove(DONT_SHOW_KEY);
        localSettings.Values.Remove(LAST_PROMPT_KEY);
        Trace.WriteLine("All review prompt settings cleared for testing");
    }
}
