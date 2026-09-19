using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vanilla_RTX_App.Core;

namespace Vanilla_RTX_App.Modules;

/// <summary>
/// One options.txt parameter and the value the app writes into it. Minecraft Bedrock's
/// options.txt is a flat <c>name:value</c> file and every setting the app has ever needed to
/// touch is an integer, so that is the whole shape - a value the game stores as a float or a
/// string is out of scope and should not be forced through here.
/// </summary>
public readonly record struct LaunchOption(string Name, int Value);

public class MinecraftLauncher
{
    /// <summary>
    /// What a fresh install launches with: ray tracing on (<c>graphics_mode 3</c>), the in-game
    /// graphics-mode switcher enabled, and VSync off for latency. This is the set the settings
    /// panel's Reset restores, and the one <see cref="ParseOptions"/> falls back to when nothing
    /// is stored yet.
    /// </summary>
    public static readonly LaunchOption[] DefaultOptions =
    {
        new("graphics_mode", 3),
        new("graphics_mode_switch", 1),
        new("gfx_vsync", 0)
    };

    /// <summary>
    /// Separator between entries in the persisted form. Not a valid character in an options.txt
    /// parameter name, so it can never appear inside one and split a name in half.
    /// </summary>
    private const char EntrySeparator = ';';

    // -------------------------------------------------------------------------
    // PUBLIC API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Launches the game with whatever options the user has configured in the settings panel
    /// (<see cref="EnvironmentVariables.Persistent.LaunchOptions"/>). This is what the Launch
    /// button calls.
    ///
    /// <para>An empty configured set is a deliberate choice, not an error: the user removed
    /// every row because they want the game launched with nothing touched. The plain protocol
    /// launch below is what that means, and it must not silently fall back to the defaults -
    /// doing so would rewrite options the user explicitly asked us to leave alone.</para>
    /// </summary>
    public static Task<string> LaunchConfiguredMinecraftRTXAsync(bool isTargetingPreview)
    {
        var options = ParseOptions(EnvironmentVariables.Persistent.LaunchOptions);

        return options.Length == 0
            ? LaunchOnlyAsync(isTargetingPreview)
            : LaunchWithOptionsAsync(isTargetingPreview, launchAfterUpdate: true, options);
    }

    /// <summary>
    /// Reads the persisted <c>name=value;name=value</c> form back into options. Anything
    /// unreadable - a missing <c>=</c>, a value that isn't an integer, an empty name - is
    /// dropped rather than failing the whole string, so one corrupt entry costs that entry and
    /// not the user's entire configuration. A null/blank string means "nothing has been saved
    /// yet" and yields <see cref="DefaultOptions"/>; an explicitly empty configuration is stored
    /// as <see cref="EmptyMarker"/> so those two cases stay distinguishable.
    /// </summary>
    public static LaunchOption[] ParseOptions(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return DefaultOptions.ToArray();

        if (stored.Trim() == EmptyMarker)
            return Array.Empty<LaunchOption>();

        var parsed = new List<LaunchOption>();

        foreach (var entry in stored.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.Split('=', 2);
            if (split.Length != 2) continue;

            var name = split[0].Trim();
            if (name.Length == 0) continue;

            // InvariantCulture, and int rather than the culture-aware parse a UI would use: this
            // string is written by the app and read back by the app, so it must not start
            // meaning something else because the user changed their regional settings.
            if (!int.TryParse(split[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                continue;

            parsed.Add(new LaunchOption(name, value));
        }

        return parsed.ToArray();
    }

    /// <summary>
    /// The inverse of <see cref="ParseOptions"/>. Entries with a blank name are skipped - the
    /// settings panel lets a row exist while it is being typed into, and a half-filled row is
    /// not a configuration.
    /// </summary>
    public static string SerializeOptions(IEnumerable<LaunchOption> options)
    {
        var text = string.Join(EntrySeparator, options
            .Where(o => !string.IsNullOrWhiteSpace(o.Name))
            .Select(o => $"{o.Name.Trim()}={o.Value.ToString(CultureInfo.InvariantCulture)}"));

        return text.Length == 0 ? EmptyMarker : text;
    }

    /// <summary>
    /// Stands in for "the user deliberately configured no options at all". A plain empty string
    /// can't say that - it is also what an unset setting looks like - and the two have to launch
    /// differently (see <see cref="LaunchConfiguredMinecraftRTXAsync"/>).
    /// </summary>
    public const string EmptyMarker = "none";

    /// <summary>
    /// Launches the game without reading or writing a single options.txt. The "no options
    /// configured" path - see <see cref="LaunchConfiguredMinecraftRTXAsync"/>.
    /// </summary>
    private static async Task<string> LaunchOnlyAsync(bool isTargetingPreview)
    {
        var versionName = MinecraftUserDataLocator.GetVersionDisplayName(isTargetingPreview);
        var messages = new List<string> { $"No launch options are configured - launching {versionName} without changing any game settings." };

        await Task.Delay(250);

        var protocol = isTargetingPreview ? "minecraft-preview://" : "minecraft://";
        TryLaunchGame(protocol, versionName, anyModificationsMade: false, messages);

        return string.Join("\n", messages);
    }

    /// <summary>
    /// General-purpose entry point: updates any number of integer options.txt
    /// parameters across every signed-in account's options.txt (and Shared, if present),
    /// optionally launching the game afterward. The settings panel is what decides which
    /// parameters those are; nothing in this file hardcodes a preset beyond
    /// <see cref="DefaultOptions"/>.
    /// </summary>
    public static async Task<string> LaunchWithOptionsAsync(
        bool isTargetingPreview,
        bool launchAfterUpdate,
        params LaunchOption[] updates)
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
        LaunchOption[] updates)
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
            // TODO: Maybe make it so it restores the backup if anything goes wrong, and make the backup be the
            // version that is made once and never overriden, this idea is rough, think it through later.
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
