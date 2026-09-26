using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules;

namespace Vanilla_RTX_App.Core.FileActivation;

/// <summary>
/// The <c>vanillartx://</c> commands that do something rather than open something, and run
/// without the window: <c>vanillartx://launchminecraftrtx</c> as a desktop shortcut starts the
/// game and the app never appears.
///
/// <para><b>Two ways to run, one command.</b> On a cold launch, <see cref="TryRunWithoutWindowAsync"/>
/// runs it before MainWindow is ever constructed, and App exits straight after. When the app is
/// already open, the link is handed to that instance as usual and runs through
/// <see cref="SilentLink.InWindow"/> - quietly, logged, nothing raised. Either way, <b>a command
/// that fails becomes visible</b>: a cold run that returns false falls through into an ordinary
/// startup, which routes the same link again, now in a window whose log can say why; a failure
/// in a running instance raises it. Silent when it works, in front of the user when it has
/// something to tell them.</para>
///
/// <para><b>What earns a place here</b>, since the class doc on MainWindow.LinkCommands.cs
/// otherwise holds that a link never acts, and any web page can hand one to a user:</para>
/// <list type="bullet">
/// <item>It does only what the user already set up. Launching applies the launch options they
/// configured in Settings, and nothing else - a link cannot supply options of its own.</item>
/// <item>It installs, deletes and tunes nothing, and nothing it does needs undoing.</item>
/// <item><b>It is idempotent.</b> Windows can deliver one activation more than once, and a
/// cold run and the running instance are different processes, so nothing can de-duplicate
/// deliveries reliably. Running it twice has to be harmless - a second launch finds the options
/// already written and the game already starting.</item>
/// <item>It is gated by a real MainWindow control (<see cref="SilentLink.ControlName"/>): in a
/// window, that control has to be enabled, exactly as for a click; without one, the remote
/// <c># SuspendControls</c> list is checked by the same name, so a suspended feature can't be
/// reached around the window either.</item>
/// </list>
///
/// <para><b>A cold run gets only what it sets up itself</b> - settings loaded and the user-data
/// locator run, the two things launching needs. Nothing it learns is saved; a run that falls
/// through leaves MainWindow to load and validate everything again from scratch, as it would on
/// any launch. A new command that needs more of the app than that (the GDK locator, the pack
/// list) should set it up in its own <see cref="SilentLink.WithoutWindow"/> rather than here.</para>
/// </summary>
internal static class SilentLinks
{
    /// <param name="Label">For log lines.</param>
    /// <param name="ControlName">The x:Name of the MainWindow control whose availability gates this command.</param>
    /// <param name="WithoutWindow">The cold run. True if it did what it was asked; false hands it to a window.</param>
    /// <param name="InWindow">The run inside an open app, free to log. True if it did what it was asked.</param>
    internal readonly record struct SilentLink(
        string Label,
        string ControlName,
        Func<Task<bool>> WithoutWindow,
        Func<MainWindow, Task<bool>> InWindow);

    /// <summary>
    /// By command name, matched the way MainWindow's module links are (see
    /// <see cref="FileActivationRouter.ParseLink"/>). The README's link list has to agree with this table.
    /// </summary>
    private static readonly Dictionary<string, SilentLink> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["launchminecraftrtx"] = new("Launch Minecraft RTX", "LaunchMinecraftButton",
            () => LaunchWithoutWindowAsync(isPreview: false), w => w.LaunchMinecraftAsync(isPreview: false)),
        ["launchminecraftrtxpreview"] = new("Launch Minecraft Preview RTX", "LaunchMinecraftButton",
            () => LaunchWithoutWindowAsync(isPreview: true), w => w.LaunchMinecraftAsync(isPreview: true)),
    };

    /// <summary>The silent command <paramref name="link"/> names, or null for a link that navigates.</summary>
    internal static SilentLink? Find(Uri link) =>
        Commands.TryGetValue(FileActivationRouter.ParseLink(link).Command, out var command) ? command : null;

    /// <summary>
    /// Runs <paramref name="command"/> with no window at all. Never throws. False for anything
    /// short of success - including a remotely suspended control, which the window then reports
    /// the way it reports any unavailable button.
    /// </summary>
    internal static async Task<bool> TryRunWithoutWindowAsync(SilentLink command)
    {
        // App's constructor already applied the cached announcements synchronously, so this is
        // the same list MainWindow would suspend controls from.
        if (TeletextContent.SuspendControls?.Contains(command.ControlName, StringComparer.OrdinalIgnoreCase) == true)
        {
            Trace.WriteLine($"[SilentLinks] '{command.ControlName}' is remotely suspended; handing {command.Label} to the window.");
            return false;
        }

        try
        {
            EnvironmentVariables.LoadSettings();
            MinecraftUserDataLocator.ValidateAndUpdateCachedLocations();

            var done = await command.WithoutWindow();
            Trace.WriteLine($"[SilentLinks] {command.Label} without a window: {(done ? "done" : "handing to the window")}.");
            return done;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SilentLinks] {command.Label} without a window threw: {ex}");
            return false;
        }
    }

    /// <summary>
    /// A user-data folder that can't be found is left to the window, whose
    /// <c>RequireValidUserData</c> tells the user how to fix it. Anything else the launcher had to
    /// say while still launching is written to Trace; there is nowhere else for it to go.
    /// </summary>
    private static async Task<bool> LaunchWithoutWindowAsync(bool isPreview)
    {
        if (MinecraftUserDataLocator.GetDataRoot(isPreview) is null) return false;

        var outcome = await MinecraftLauncher.LaunchConfiguredMinecraftRTXAsync(isPreview);
        Trace.WriteLine($"[SilentLinks] {outcome.Log}");
        return outcome.Launched;
    }
}
