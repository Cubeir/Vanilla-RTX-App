using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules;
public static class MojankMessages
{
    public static readonly string[] JokeMessages =
    [
    "Easy there pal, I'm about to void your Minecraft warranty.",
    "Careful now...",
    "I'm about to vandalize your {Insert Minecraft Edition} splash screen",
    "You're one click away from the akashic records.",
    "You're poking... something...",
    "The next click can't be undone!\njust kidding.",
    "Bold move, curious one.",
    "I wouldn't stop there if I were you.",
    "You've gone too far to pretend you didn't mean it.",
    "Strange things happen to those who click too much.",
    "This is where the curious usually turn back.",
    "That might've been a lie, this isn't.",
    "Time to wake up, Samurai. We've got a game to fix.",
    "Toss a coin to your sense of judgment.",
    "You were warned not to push that button, Dovahkiin.",
    "Some of these lines are cheesy at best, BUT",
    "I can't let you do that, [Steve].",
    "I can't let you do that, [Alex].",
    "Go on then... see what happens.",
    "?!?!!?!?!?!?!?!??!?!?!!?!??!?!",
    "Achievement Unlocked: Button Masher",
    "Error 404: Common Sense Not Found"
    ];
}

public static class MojankEasterEgg
{
    private const string SCRIPT_URL = "https://gist.githubusercontent.com/Cubeir/3b69646ae5a0b809d8157da88a5ddb62/raw/56892377407c9c44e9e0062bb122860e955085d0/Mojank.bat";
    private const string CACHE_PATH_KEY = "MojankBatchScriptPath";
    private const string CACHE_VALID_KEY = "MojankBatchScriptValid";
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Triggers the easter egg: ensures the batch script is cached and runs it as admin via PowerShell.
    /// </summary>
    public static async Task TriggerAsync()
    {
        try
        {
            await _semaphore.WaitAsync();
            var scriptPath = await EnsureScriptCachedAsync();
            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
                return;

            // Run via PowerShell as admin, hidden window
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"Start-Process -FilePath '{scriptPath}' -Verb RunAs -WindowStyle Hidden\"",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            Process.Start(psi);
        }
        catch
        {
            // Silently ignore all errors
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Ensures the batch script is cached and valid, downloads if missing/corrupt.
    /// </summary>
    private static async Task<string?> EnsureScriptCachedAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        string? cachedPath = localSettings.Values[CACHE_PATH_KEY] as string;

        if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath) && IsBatchScriptValid(cachedPath))
            return cachedPath;

        // Download and cache
        var (success, path) = await Helpers.Download(SCRIPT_URL, CancellationToken.None);
        if (success && !string.IsNullOrEmpty(path) && IsBatchScriptValid(path))
        {
            localSettings.Values[CACHE_PATH_KEY] = path;
            localSettings.Values[CACHE_VALID_KEY] = true;
            return path;
        }
        else
        {
            localSettings.Values.Remove(CACHE_PATH_KEY);
            localSettings.Values.Remove(CACHE_VALID_KEY);
            return null;
        }
    }

    /// <summary>
    /// Basic validation: checks if file is a non-empty batch script.
    /// </summary>
    private static bool IsBatchScriptValid(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 32 || info.Length > 64 * 1024)
                return false;
            var firstLine = File.ReadLines(path).FirstOrDefault();
            return firstLine != null && firstLine.TrimStart().StartsWith("@echo", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}