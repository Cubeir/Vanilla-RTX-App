using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules;

public sealed partial class ReviewPromptControl : UserControl
{
    public event EventHandler Closed;

    public ReviewPromptControl()
    {
        this.InitializeComponent();

        ReviewButton.Click += ReviewButton_Click;
        LaterButton.Click += LaterButton_Click;
        NeverButton.Click += NeverButton_Click;
        DonationLink.Click += DonationLink_Click;

        // Make sure the control takes full size of parent
        this.HorizontalAlignment = HorizontalAlignment.Stretch;
        this.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private async void DonationLink_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        // Donation link does NOT close the dialog - user can click it freely
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://ko-fi.com/cubeir"));
    }

    private void RootGrid_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Clicking the backdrop (outside dialog) = "Show later"
        _ = ReviewPromptManager.ResetTimerAsync();
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
        await ReviewPromptManager.MarkAsCompletedAsync();
        Hide();
    }

    private async void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        await ReviewPromptManager.ResetTimerAsync();
        Hide();
    }

    private async void NeverButton_Click(object sender, RoutedEventArgs e)
    {
        await ReviewPromptManager.NeverShowAgainAsync();
        Hide();
    }

    public void Show()
    {
        RootGrid.Visibility = Visibility.Visible;
        System.Diagnostics.Trace.WriteLine("RootGrid visibility set to Visible");
    }

    public void Hide()
    {
        RootGrid.Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}

public static class ReviewPromptManager
{
    private const string FIRST_LAUNCH_KEY = "ReviewPromptFirstLaunchTime";
    private const string DONT_SHOW_KEY = "ReviewPromptDontShowReviewPrompt";
    private const string LAST_PROMPT_KEY = "ReviewPromptLastPromptTime";
    private const double MINUTES_BEFORE_PROMPT = 3600; // how many hours to wait before showing for the first time, or showing again
    private const int SHOW_DELAY_SECONDS = 1; // delay to show it after being called

    private static ReviewPromptControl? _currentPrompt;
    private static Panel? _rootPanel;

    /// <summary>
    /// Initialize and show the review prompt if conditions are met.
    /// Call this once on app startup.
    /// </summary>
    /// <param name="rootPanel">The root panel of your MainWindow (e.g., the main Grid)</param>
    public static async Task InitializeAsync(Panel rootPanel)
    {
        System.Diagnostics.Trace.WriteLine("=== ReviewPrompt: InitializeAsync called ===");
        _rootPanel = rootPanel;

        if (_rootPanel == null)
        {
            System.Diagnostics.Trace.WriteLine("ERROR: rootPanel is NULL!");
            return;
        }

        System.Diagnostics.Trace.WriteLine($"Root panel type: {_rootPanel.GetType().Name}");

        // Record first launch if not already recorded
        await RecordFirstLaunchIfNeededAsync();

        // Check if we should show the prompt
        bool shouldShow = await ShouldShowPromptAsync();
        System.Diagnostics.Trace.WriteLine($"Should show prompt: {shouldShow}");

        if (shouldShow)
        {
            System.Diagnostics.Trace.WriteLine($"Waiting {SHOW_DELAY_SECONDS} seconds before showing...");
            await Task.Delay(TimeSpan.FromSeconds(SHOW_DELAY_SECONDS));
            System.Diagnostics.Trace.WriteLine("Calling ShowPrompt()...");
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
            System.Diagnostics.Trace.WriteLine($"First launch recorded: {now} ticks ({DateTime.UtcNow})");
        }
        else
        {
            System.Diagnostics.Trace.WriteLine($"First launch already recorded: {localSettings.Values[FIRST_LAUNCH_KEY]} ticks");
        }
    }

    private static async Task<bool> ShouldShowPromptAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;

        // Check if user said "Don't show again"
        if (localSettings.Values.ContainsKey(DONT_SHOW_KEY))
        {
            System.Diagnostics.Trace.WriteLine("Don't show key exists - returning false");
            return false;
        }

        // Get first launch time
        if (!localSettings.Values.ContainsKey(FIRST_LAUNCH_KEY))
        {
            System.Diagnostics.Trace.WriteLine("No first launch key - returning false");
            return false;
        }

        var firstLaunchTicks = localSettings.Values[FIRST_LAUNCH_KEY];
        if (firstLaunchTicks == null || !(firstLaunchTicks is long))
        {
            System.Diagnostics.Trace.WriteLine($"Invalid first launch ticks: {firstLaunchTicks}");
            return false;
        }

        var firstLaunch = new DateTime((long)firstLaunchTicks, DateTimeKind.Utc);
        System.Diagnostics.Trace.WriteLine($"First launch: {firstLaunch} UTC");

        // Check if time has passed since first launch (or last "Show later")
        DateTime checkTime = firstLaunch;

        if (localSettings.Values.ContainsKey(LAST_PROMPT_KEY))
        {
            var lastPromptTicks = localSettings.Values[LAST_PROMPT_KEY];
            if (lastPromptTicks is long)
            {
                var lastPrompt = new DateTime((long)lastPromptTicks, DateTimeKind.Utc);
                checkTime = lastPrompt;
                System.Diagnostics.Trace.WriteLine($"Using last prompt time: {lastPrompt} UTC");
            }
        }

        var minutesSince = (DateTime.UtcNow - checkTime).TotalMinutes;
        System.Diagnostics.Trace.WriteLine($"Minutes since check time: {minutesSince} (need {MINUTES_BEFORE_PROMPT})");
        System.Diagnostics.Trace.WriteLine($"Current UTC: {DateTime.UtcNow}");

        return minutesSince >= MINUTES_BEFORE_PROMPT;
    }

    private static void ShowPrompt()
    {
        System.Diagnostics.Trace.WriteLine("=== ShowPrompt() called ===");

        if (_rootPanel == null)
        {
            System.Diagnostics.Trace.WriteLine("ERROR: _rootPanel is NULL in ShowPrompt!");
            return;
        }

        if (_currentPrompt != null)
        {
            System.Diagnostics.Trace.WriteLine("Prompt already showing!");
            return;
        }

        System.Diagnostics.Trace.WriteLine("Creating new ReviewPromptControl...");
        _currentPrompt = new ReviewPromptControl();

        // ensure it appears on top
        Canvas.SetZIndex(_currentPrompt, 9999);

        // span all columns and rows beucase your mainwindow has 2 sections
        if (_rootPanel is Grid)
        {
            Grid.SetColumnSpan(_currentPrompt, int.MaxValue);
            Grid.SetRowSpan(_currentPrompt, int.MaxValue);
            System.Diagnostics.Trace.WriteLine("Set ColumnSpan and RowSpan to cover entire Grid");
        }

        _currentPrompt.Closed += (s, e) =>
        {
            System.Diagnostics.Trace.WriteLine("Prompt closed event fired");
            if (_rootPanel.Children.Contains(_currentPrompt))
            {
                _rootPanel.Children.Remove(_currentPrompt);
            }
            _currentPrompt = null;
        };

        System.Diagnostics.Trace.WriteLine("Adding prompt to root panel...");
        _rootPanel.Children.Add(_currentPrompt);

        System.Diagnostics.Trace.WriteLine($"Current children count: {_rootPanel.Children.Count}");

        System.Diagnostics.Trace.WriteLine("Calling Show() on prompt...");
        _currentPrompt.Show();

        System.Diagnostics.Trace.WriteLine("=== ShowPrompt() complete ===");
    }

    internal static async Task ResetTimerAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[LAST_PROMPT_KEY] = DateTime.UtcNow.Ticks;
        System.Diagnostics.Trace.WriteLine("Timer reset - will show again after delay");
    }

    internal static async Task NeverShowAgainAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[DONT_SHOW_KEY] = true;
        System.Diagnostics.Trace.WriteLine("Never show again flag set");
    }

    internal static async Task MarkAsCompletedAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[DONT_SHOW_KEY] = true;
        System.Diagnostics.Trace.WriteLine("Review completed - will not show again");
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
        System.Diagnostics.Trace.WriteLine("All review prompt settings cleared for testing");
    }
}
