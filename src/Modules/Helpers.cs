using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Vanilla_RTX_App.Modules;

/// <summary>
/// Shared building blocks with no single owner, split by topic into partial files so each one
/// can be read on its own: this file (text, pickers, session flags), <c>Helpers.Images.cs</c>
/// (reading and writing images), <c>Helpers.Network.cs</c> (HTTP and downloads) and
/// <c>Helpers.Files.cs</c> (file search, zips, elevated replace, pack bookkeeping). Being one
/// class across files is deliberate: every call site stays <c>Helpers.X</c>, whichever file X
/// lives in.
///
/// <para>The standalone classes that used to share Helpers.cs have files of their own:
/// <see cref="MinecraftGDKLocator"/> and <see cref="MinecraftUserDataLocator"/> in
/// <c>MinecraftLocators.cs</c>, <see cref="TextureSetHelper"/> in <c>TextureSetHelper.cs</c>,
/// and <see cref="FastBitmap"/> beside the image readers in <c>Helpers.Images.cs</c>.</para>
/// </summary>
public static partial class Helpers
{
    /// <summary>
    /// Removes Minecraft's section-sign formatting codes (§a, §l, §r...) from a pack name or
    /// description, leaving the text the player actually reads.
    ///
    /// Deliberately permissive about what follows the sign: Bedrock keeps adding codes
    /// (§g and §h-§u arrived well after the classic §0-§f / §k-§r set), so matching a fixed
    /// character class would quietly start leaving new ones behind. A lone trailing § is
    /// dropped too, and a § before whitespace takes only itself - "Cost: 5§ each" keeps its
    /// space. Trailing whitespace is trimmed, since a code at either end leaves some behind.
    ///
    /// Lives here rather than in either caller: PackBrowserOverlay needs it for display,
    /// Alchitex needs it for the name it writes into a regenerated manifest, and both had
    /// their own version that disagreed on all three edge cases above.
    /// </summary>
    public static string StripMinecraftFormatting(string input)
        => string.IsNullOrEmpty(input) ? input : MinecraftFormattingCodeRegex.Replace(input, string.Empty).Trim();

    private static readonly Regex MinecraftFormattingCodeRegex = new(@"§\S?", RegexOptions.Compiled);

    /// <summary>
    /// Shortns it too
    /// </summary>
    public static string SanitizePathForDisplay(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return fullPath;

        try
        {
            // Find LocalState in the path
            int localStateIndex = fullPath.IndexOf("LocalState", StringComparison.OrdinalIgnoreCase);

            if (localStateIndex > 0)
            {
                // Get everything after "LocalState"
                string afterLocalState = fullPath.Substring(localStateIndex);
                return $"Data\\{afterLocalState}";
            }

            // If LocalState not found, just return the filename and parent folder
            var fileName = Path.GetFileName(fullPath);
            var parentFolder = Path.GetFileName(Path.GetDirectoryName(fullPath));
            return $"...\\{parentFolder}\\{fileName}";
        }
        catch
        {
            // Fallback to just showing the last two segments
            try
            {
                var fileName = Path.GetFileName(fullPath);
                var parentFolder = Path.GetFileName(Path.GetDirectoryName(fullPath));
                return $"...\\{parentFolder}\\{fileName}";
            }
            catch
            {
                return fullPath;
            }
        }
    }


    /// <summary>
    /// Shows a folder picker owned by <paramref name="windowHandle"/> and returns the chosen
    /// path, or null if the user cancelled. Every folder the app asks for goes through here.
    ///
    /// <para><b>It uses <c>Microsoft.Windows.Storage.Pickers.FolderPicker</c>, not the WinRT
    /// one, for exactly one reason: <c>SuggestedStartFolder</c>.</b> WinRT's picker can only
    /// be aimed at a well-known location from a fixed enum, so "open where the path you are
    /// about to change already points" is not expressible with it - and the alternative is
    /// making the user navigate back to a folder the app already knows.</para>
    ///
    /// <para><paramref name="startAtPath"/> is only applied when it exists on disk. A stale
    /// path is the normal case here (it is usually *why* the user is re-picking), and handing
    /// the shell a folder that is gone is how a picker opens somewhere arbitrary instead of
    /// its own default.</para>
    /// </summary>
    public static async Task<string?> PickFolderAsync(IntPtr windowHandle, string? startAtPath = null)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle));

            if (!string.IsNullOrWhiteSpace(startAtPath) && Directory.Exists(startAtPath))
                picker.SuggestedStartFolder = startAtPath;

            var result = await picker.PickSingleFolderAsync();
            return string.IsNullOrEmpty(result?.Path) ? null : result.Path;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Helpers] Folder picker failed: {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Checks if Minecraft.Windows process is running, returns true if so
    /// </summary>
    public static bool IsMinecraftRunning()
    {
        var mcProcesses = Process.GetProcessesByName("Minecraft.Windows");
        return mcProcesses.Length > 0;
    }

    /// <summary>
    /// Returns one of 3 special occasion names (me and my loved one's "birthday"s, "christmas", or "pumpkin" during weekends of October)
    /// </summary>
    public static string? GetSpecialOccasionName()
    {
        var date = DateTime.Today;
        if (date.Month == 4 && date.Day >= 21 && date.Day <= 23)
            return "birthday";
        if (date.Month == 10 && (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday))
            return "pumpkin";
        if ((date.Month == 12 && date.Day >= 23) || (date.Month == 1 && date.Day <= 7))
            return "christmas";
        return null;
    }


    /// <summary>
    /// "Have I already done this in this session?", keyed by an arbitrary string. Process
    /// lifetime only - nothing is persisted, so every launch starts clean.
    ///
    /// <para>Typical use is a message shown once and not repeated:
    /// <c>Set("key") ? longExplanation : ""</c>, which relies on <see cref="Set"/> returning
    /// true exactly once. Not thread-safe; call from the UI thread.</para>
    /// </summary>
    public static class RuntimeFlags
    {
        private static readonly HashSet<string> _flags = new();

        /// <summary>
        /// Whether the flag is set, without setting it. Use this to <i>test</i>;
        /// <see cref="Set"/> is the one that claims.
        /// </summary>
        public static bool Has(string key) => _flags.Contains(key);

        /// <summary>
        /// Claims the flag. <b>True means the caller is the first</b> and should do the
        /// once-per-session thing; false means someone already has. Calling this in a
        /// condition is the intended use, so be aware it is not a pure test - it sets.
        /// </summary>
        public static bool Set(string key)
        {
            try
            {
                if (_flags.Contains(key))
                    return false;

                _flags.Add(key);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RUNETIMEFLAGS] Something went wrong: {ex.ToString}");
                return false;
            }
        }

        /// <summary>
        /// Forgets the flag, so the next <see cref="Set"/> claims it again. True if it was
        /// set. For a state that can legitimately recur within one session.
        /// </summary>
        public static bool Unset(string key) => _flags.Remove(key);
    }
}
