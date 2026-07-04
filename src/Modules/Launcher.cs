using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Vanilla_RTX_App.Modules;

public class MinecraftLauncher
{
    // -------------------------------------------------------------------------
    // PUBLIC API — semantic wrappers around the general options.txt updater
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enables ray tracing (graphics_mode 3 + mode switch) and disables VSync,
    /// then launches the game. This is the default LaunchButton_Click behavior.
    /// </summary>
    public static Task<string> LaunchMinecraftRTXAsync(bool isTargetingPreview)
        => LaunchWithOptionsAsync(
            isTargetingPreview,
            launchAfterUpdate: true,
            ("graphics_mode", 3),
            ("graphics_mode_switch", 1),
            ("gfx_vsync", 0)
        );

    /// <summary>
    /// Same as regular but with vsync
    /// </summary>
    public static Task<string> LaunchMinecraftStandardAsync(bool isTargetingPreview)
        => LaunchWithOptionsAsync(
            isTargetingPreview,
            launchAfterUpdate: true,
            ("graphics_mode", 3),
            ("graphics_mode_switch", 1),
            ("gfx_vsync", 1)
        );

    /// <summary>
    /// General-purpose entry point: updates any number of integer options.txt
    /// parameters across every signed-in account's options.txt (and Shared, if present),
    /// optionally launching the game afterward. This is what both presets above call —
    /// add a new preset by adding a new method that forwards into this with different
    /// (param name, value) tuples; no need to touch the engine itself.
    /// </summary>
    public static async Task<string> LaunchWithOptionsAsync(
        bool isTargetingPreview,
        bool launchAfterUpdate,
        params (string ParamName, int Value)[] updates)
    {
        if (updates == null || updates.Length == 0)
            return "❗ No options were specified to update.";

        var versionName = MinecraftUserDataLocator.GetVersionDisplayName(isTargetingPreview);

        if (!MinecraftUserDataLocator.IsDataValid(isTargetingPreview))
        {
            return $"❗ {versionName} data folder not found.\n" +
                   "Make sure the correct version of the game is installed and has been launched at least once.";
        }

        var optionsFiles = MinecraftUserDataLocator.FindAllOptionsFiles(isTargetingPreview);
        if (optionsFiles.Length == 0)
        {
            return $"❗ No options.txt files found for {versionName}.\n" +
                   "Make sure the game has been launched at least once.";
        }

        var allStatusMessages = new List<string>();
        var filesProcessed = 0;
        var anyModificationsMade = false;

        foreach (var optionsFilePath in optionsFiles)
        {
            var ownerLabel = MinecraftUserDataLocator.GetOwningFolderLabel(isTargetingPreview, optionsFilePath);
            var (success, modified, messages) = await TryUpdateOptionsFileAsync(optionsFilePath, ownerLabel, updates);

            allStatusMessages.AddRange(messages);

            if (success)
            {
                filesProcessed++;
                if (modified)
                    anyModificationsMade = true;
            }
        }

        if (filesProcessed == 0)
            return string.Join("\n", allStatusMessages.Append("❗ No options files could be processed due to access issues."));

        allStatusMessages.Add($"Processed {filesProcessed} options file(s).");

        if (!launchAfterUpdate)
            return string.Join("\n", allStatusMessages);

        await Task.Delay(250);

        var protocol = isTargetingPreview ? "minecraft-preview://" : "minecraft://";
        TryLaunchGame(protocol, versionName, anyModificationsMade, allStatusMessages);

        return string.Join("\n", allStatusMessages);
    }

    // -------------------------------------------------------------------------
    // PRIVATE HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies all requested updates to a single options.txt file: validates
    /// accessibility, clears read-only, backs up (.backup, overwritten each run —
    /// purely a "don't curse me" safety net, never read back by the app), applies
    /// each update line-by-line (appending any parameter not already present),
    /// then writes the file back.
    /// </summary>
    private static async Task<(bool success, bool modified, List<string> messages)> TryUpdateOptionsFileAsync(
        string optionsFilePath,
        string ownerLabel,
        (string ParamName, int Value)[] updates)
    {
        var messages = new List<string>();

        // Accessibility check
        try
        {
            using var fileStream = File.Open(optionsFilePath, FileMode.Open, FileAccess.ReadWrite);
        }
        catch (UnauthorizedAccessException)
        {
            messages.Add($"❗ Access denied to [{ownerLabel}] options file");
            return (false, false, messages);
        }
        catch (IOException ex)
        {
            messages.Add($"❗ File inaccessible [{ownerLabel}]: {ex.Message}");
            return (false, false, messages);
        }

        // Clear read-only attribute if set
        try
        {
            var fileInfo = new FileInfo(optionsFilePath);
            if (fileInfo.IsReadOnly)
                fileInfo.IsReadOnly = false;
        }
        catch (Exception ex)
        {
            messages.Add($"❗ Failed to remove readonly attribute [{ownerLabel}]: {ex.Message}");
            return (false, false, messages);
        }

        try
        {
            var lines = (await File.ReadAllLinesAsync(optionsFilePath)).ToList();
            var fileModified = false;

            foreach (var (paramName, value) in updates)
            {
                var applied = ApplyOption(lines, paramName, value, ownerLabel, messages);
                fileModified |= applied;
            }

            // Backup is purely defensive — never read back by the app, just a
            // safety net so the user has somewhere to go if something looks wrong.
            var backupPath = optionsFilePath + ".backup";
            File.Copy(optionsFilePath, backupPath, true);

            await File.WriteAllLinesAsync(optionsFilePath, lines);

            return (true, fileModified, messages);
        }
        catch (Exception ex)
        {
            messages.Add($"❗ Failed to update [{ownerLabel}] options file: {ex.Message}");
            return (false, false, messages);
        }
    }

    /// <summary>
    /// Updates a single "paramName:value" line in-place if present (only touching it
    /// when the value actually differs), or appends a new line if the parameter
    /// doesn't exist yet in this options.txt. Returns true if the line list was changed.
    /// </summary>
    private static bool ApplyOption(List<string> lines, string paramName, int value, string ownerLabel, List<string> messages)
    {
        var prefix = paramName + ":";

        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = lines[i].Split(':');
            if (parts.Length > 1)
            {
                var oldValue = parts[1].Trim();
                var newValue = value.ToString();

                if (oldValue == newValue)
                    return false; // already correct, nothing to do

                lines[i] = $"{paramName}:{newValue}";
                messages.Add($"[{ownerLabel}] {paramName}: {oldValue} -> {newValue}");
                return true;
            }

            // Malformed line with the right prefix but no value — overwrite cleanly
            lines[i] = $"{paramName}:{value}";
            messages.Add($"[{ownerLabel}] {paramName}: (malformed) -> {value}");
            return true;
        }

        // Parameter wasn't present at all — append it
        lines.Add($"{paramName}:{value}");
        messages.Add($"[{ownerLabel}] Added {paramName}:{value}");
        return true;
    }

    /// <summary>
    /// Launches the game via protocol activation. Failures are reported but never
    /// thrown — if options were already updated successfully, the user is told to
    /// launch manually rather than losing that progress to an unrelated launch failure.
    /// </summary>
    private static void TryLaunchGame(string protocol, string versionName, bool anyModificationsMade, List<string> allStatusMessages)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = protocol,
                UseShellExecute = true,
                ErrorDialog = false
            };

            Process.Start(processInfo);
            allStatusMessages.Add($"✅ Settings updated and launched {versionName} successfully.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            allStatusMessages.Add($"Failed to launch {versionName}: {ex.Message}");
            if (anyModificationsMade)
                allStatusMessages.Add("⚠️ Settings were updated successfully — you should now launch the game manually.");
        }
        catch (Exception ex)
        {
            allStatusMessages.Add($"Unexpected error launching {versionName}: {ex.Message}");
            if (anyModificationsMade)
                allStatusMessages.Add("⚠️ Settings were updated successfully — you should now launch the game manually.");
        }
    }
}
