using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;

namespace Vanilla_RTX_App;

/// <summary>
/// What actually happens to a file opened from Explorer once
/// <see cref="Core.FileActivationRouter"/> has decided where it belongs.
///
/// <para><b>These live on MainWindow because what they do is drive it:</b> its log carries
/// every per-file message from the import, its progress bar runs for the duration, its
/// buttons are disabled while it runs, and its open child windows are refreshed afterwards so
/// they aren't left showing a list built before the import landed. All of that is private to
/// the window; a partial keeps it that way.</para>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Serializes .mcpack imports specifically - see ImportBetterRTXPresetFilesAsync's own
    /// RtpackImportLock for why .rtpack gets a separate one rather than sharing this: they
    /// deploy to completely unrelated folders (Minecraft's resource_packs vs. this app's own
    /// RTX_Cache) and share no state, so there's no reason a slow .rtpack import should make
    /// an .mcpack drop wait, or vice versa.
    ///
    /// What this guards against: Windows can (and, observed firsthand, does) activate the FTA
    /// handler more than once for a single multi-select "Open with" - one process wins the
    /// launch mutex and imports its files directly, but a second process can lose that race
    /// and still be carrying the very same file list, which it hands off to the first through
    /// the wake-event/pending-file mechanism in App.xaml.cs. App.xaml.cs's own
    /// FilterRecentlyHandledFiles is the first line of defence against that - it drops a
    /// duplicate file list before either import path ever sees it - but this lock is what
    /// keeps two *different* concurrent .mcpack requests (not duplicates of each other, just
    /// two genuinely separate drops close together) from interleaving into the same Log()
    /// output and racing ExpImpDel's own duplicate-UUID check, which reads the destination
    /// folder to see if a pack is already installed and can be fooled by a read landing
    /// before an unrelated concurrent import has finished writing its own manifest.
    ///
    /// Held for the full duration of a batch, not per file - so a second request arriving
    /// mid-batch queues behind the entire first one rather than interleaving with it, which is
    /// what actually guarantees "one at a time" instead of merely "one file at a time within
    /// whichever call happens to be running".
    /// </summary>
    private static readonly SemaphoreSlim McpackImportLock = new(1, 1);

    /// <summary>
    /// Entry point for .mcpack file-type-association activation (see App.xaml.cs), and
    /// deliberately independent of PackBrowserWindow rather than opening one on the user's
    /// behalf: a window appearing on top of a window nobody asked for, and PackBrowserWindow's
    /// own "no Minecraft data location yet" fallback putting a folder picker in front of them
    /// unprompted - reachable here because MainWindow_Loaded resolves that cache
    /// asynchronously and this can run first.
    ///
    /// <para>Calls ExpImpDel directly instead - the same utility PackBrowserWindow's Add-pack
    /// button and drag-and-drop both reach downstream - and reports through the same Log() the
    /// rest of the window uses, one pack at a time, no windows and no pickers either way.</para>
    ///
    /// Per-file messages come straight from ExpImpDel.ImportStatusChanged rather than a
    /// generic pass/fail here - PackBrowserWindow already subscribes to the exact same event
    /// for its own status text, so this reuses the same wording a manual import would show
    /// ("duplicate already installed", "not identified as a resource pack", etc.) instead of
    /// inventing a second, vaguer vocabulary for the headless path.
    /// </summary>
    public async Task ImportPackFilesAsync(IReadOnlyList<string> filePaths)
    {
        string[] ToDisable = ["BrowsePacksButton"];

        var paths = filePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return;

        // Waits for MainWindow_Loaded to have actually resolved the Minecraft data location
        // - see the remarks above for why guessing at a fixed delay isn't good enough here.
        await WaitUntilInitializedAsync();

        await McpackImportLock.WaitAsync();
        _progressManager.ShowProgress();
        try
        {
            WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

            var names = paths.Select(p => Path.GetFileNameWithoutExtension(p) ?? p).ToList();
            Log($"Starting to import:\n{string.Join(Environment.NewLine, names)}", LogLevel.Import);

            void OnStatus(string message) => Log(message, LogLevel.Import);
            ExpImpDel.ImportStatusChanged += OnStatus;

            // Same confirmation dialogs PackBrowserWindow shows for a non-resource/duplicate
            // pack - ConfirmOverwrite/ConfirmNonResourceImport are static and global, so save
            // and restore whatever PackBrowserWindow (if open) left there rather than
            // clobbering it for the duration of this import.
            var previousConfirmOverwrite = ExpImpDel.ConfirmOverwrite;
            var previousConfirmNonResourceImport = ExpImpDel.ConfirmNonResourceImport;
            ExpImpDel.ConfirmOverwrite = (packName, existingPath) => ImportDialogs.ShowOverwriteDialogAsync(this, packName, existingPath);
            ExpImpDel.ConfirmNonResourceImport = packName => ImportDialogs.ShowNonResourceDialogAsync(this, packName);

            var succeeded = 0;
            try
            {
                foreach (var path in paths)
                {
                    if (await ExpImpDel.ImportFromPathsAsync(new[] { path }))
                        succeeded++;
                }
            }
            finally
            {
                ExpImpDel.ImportStatusChanged -= OnStatus;
                ExpImpDel.ConfirmOverwrite = previousConfirmOverwrite;
                ExpImpDel.ConfirmNonResourceImport = previousConfirmNonResourceImport;
            }

            Log(succeeded == paths.Count
                ? $"Finished importing {succeeded} pack{(paths.Count == 1 ? "" : "s")}."
                : $"Imported {succeeded} out of {paths.Count} pack{(paths.Count == 1 ? "" : "s")} - Use '{BrowsePacksButtonText.Text}' menu to import it manually, and see what it says.",
                succeeded == paths.Count ? LogLevel.Success : LogLevel.Warning);

            // If the user already has PackBrowserWindow open, its list was built before this
            // import landed - refresh it so it isn't left showing stale contents.
            if (succeeded > 0)
                foreach (var packBrowser in _childWindows.OfType<Modules.PackBrowser.PackBrowserWindow>())
                    await packBrowser.LoadPacksAsync();
        }
        finally
        {
            _progressManager.HideProgress();
            McpackImportLock.Release();
            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);

        }
    }

    /// <summary>
    /// Serializes .rtpack imports specifically - kept separate from McpackImportLock
    /// deliberately: .mcpack and .rtpack deploy to completely different, unrelated folders
    /// (Minecraft's own resource_packs vs. this app's RTX_Cache) and share no state, so there
    /// is no correctness reason for a slow import of one type to hold up the other. The
    /// double-activation hazard McpackImportLock's remarks describe applies here just the
    /// same, and App.xaml.cs's FilterRecentlyHandledFiles is the same first line of defence -
    /// this lock is only for genuinely separate concurrent .rtpack requests.
    /// </summary>
    private static readonly SemaphoreSlim RtpackImportLock = new(1, 1);

    /// <summary>
    /// Entry point for .rtpack file-type-association activation (see App.xaml.cs) - same
    /// idea as ImportPackFilesAsync, just for BetterRTX custom presets. Does not open
    /// BetterRTXManagerWindow: importing a preset into the app's local cache is handled by
    /// BetterRTXManagerWindow.ImportPresetFilesHeadlessAsync, which never shows a window at
    /// all (see its own remarks for why that's safe here specifically). Applying an imported
    /// preset to the game still only ever happens through the window itself, unaffected by
    /// this - this only gets a preset into the list waiting there next time it's opened.
    /// </summary>
    public async Task ImportBetterRTXPresetFilesAsync(IReadOnlyList<string> filePaths)
    {
        string[] ToDisable = ["LaunchBetterRTXManagerButton"];

        var paths = filePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return;

        await WaitUntilInitializedAsync();

        await RtpackImportLock.WaitAsync();
        _progressManager.ShowProgress();
        try
        {
            WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

            var names = paths.Select(p => Path.GetFileNameWithoutExtension(p) ?? p).ToList();
            Log($"Importing {paths.Count} BetterRTX preset{(paths.Count == 1 ? "" : "s")}:\n{string.Join(Environment.NewLine, names)}", LogLevel.BetterRTX);

            var (succeeded, total) = await Modules.BetterRTX.BetterRTXManager.ImportPresetFilesHeadlessAsync(
                paths, message => Log(message, LogLevel.BetterRTX));

            if (total == 0)
            {
                Log("No supported .rtpack file(s) to import.", LogLevel.Warning);
                return;
            }

            Log(succeeded == total
                ? $"Finished importing {succeeded} BetterRTX preset{(succeeded == 1 ? "" : "s")}.\nOpen BetterRTX Manager to install {(succeeded == 1 ? "it" : "one")}."
                : $"Imported {succeeded} out of {total} BetterRTX preset{(total == 1 ? "" : "s")} - Use BetterRTX Manager's own Add button to import manually instead.",
                succeeded == total ? LogLevel.Success : LogLevel.Warning);

            // If the user already has BetterRTXManagerWindow open, its list was built before
            // this import landed - refresh it so it isn't left showing stale contents.
            if (succeeded > 0)
                foreach (var managerWindow in _childWindows.OfType<Modules.BetterRTX.BetterRTXManagerWindow>())
                    await managerWindow.RefreshLocalPresetsAsync();
        }
        finally
        {
            _progressManager.HideProgress();
            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);
            RtpackImportLock.Release();
        }
    }

}
