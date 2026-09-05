using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

/// <summary>
/// One asset the app ships and would like to keep current: where it lives in the install
/// folder, where the newer copy comes from, and how often that is worth checking.
///
/// Both paths are stated outright rather than derived, so a future asset can come from
/// anywhere without the manifest growing a special case.
/// </summary>
public sealed record ManagedAsset(string PackagedPath, string RemoteUrl, TimeSpan Cooldown)
{
    public string FileName => Path.GetFileName(PackagedPath);
}

/// <summary>
/// Keeps a set of shipped files current without shipping a new build.
///
/// An MSIX package can't write to its own install folder, so the packaged copy is a
/// guaranteed fallback rather than the thing that gets read. Anything newer lives in
/// LocalState\Alchitex_Assets, beside the caches OnlineTexts, PackUpdater and the LUT
/// manager already keep there.
///
/// Two entry points, and the separation between them is the point:
///
///   Resolve       - what a caller reads. Cached copy if we have one, packaged copy
///                   otherwise. Never waits, never fetches, never throws.
///   TriggerUpdate - fire-and-forget from startup. Doesn't know or care who reads the files.
///
/// Deliberately a nice-to-have. Every failure path ends in "use what we already have", so a
/// user who is offline, rate-limited or behind a captive portal gets exactly the behaviour
/// they had before this existed.
///
/// Files, not formats: the job is to fetch a file completely and swap it in atomically.
/// Whether the bytes are JSON or a zip is the loaders' problem, and every one of them
/// already degrades gracefully on a file it can't read.
///
/// TO ADD AN ASSET: one entry below, plus the file in Assets/ and in the csproj (§7 of
/// CLAUDE.md). Nothing else - the update loop walks Managed, and callers read through
/// Resolve.
///
/// Lives in Alchitex because that is who needs it today; nothing in the mechanism is
/// Alchitex-specific, so the manifest is the only part another module would have to bring.
/// </summary>
public static class AssetUpdater
{
    // ── Manifest ─────────────────────────────────────────────────────────────
    //
    // Cooldowns are per asset and set by how often the file actually changes. That also
    // desynchronises them: after the first run they come due on different days,
    // so a frequent user rarely makes more than one request per launch.

    public static readonly ManagedAsset MaterialsJson = new(
        Path.Combine(AppContext.BaseDirectory, "Modules", "Alchitex", "Assets", "materials.json"),
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/materials.json",
        TimeSpan.FromDays(2));

    public static readonly ManagedAsset PbrBlacklistJson = new(
        Path.Combine(AppContext.BaseDirectory, "Modules", "Alchitex", "Assets", "pbr_blacklist.json"),
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/pbr_blacklist.json",
        TimeSpan.FromDays(6));

    public static readonly ManagedAsset WaterFallbackZip = new(
        Path.Combine(AppContext.BaseDirectory, "Modules", "Alchitex", "Assets", "water-fallback.zip"),
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/water-fallback.zip",
        TimeSpan.FromDays(18));

    public static readonly ManagedAsset FogZip = new(
        Path.Combine(AppContext.BaseDirectory, "Modules", "Alchitex", "Assets", "vanilla-rtx-fog.zip"),
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/vanilla-rtx-fog.zip",
        TimeSpan.FromDays(9));

    private static readonly ManagedAsset[] Managed =
    {
        MaterialsJson, PbrBlacklistJson, WaterFallbackZip, FogZip,
    };

    // ── Config ───────────────────────────────────────────────────────────────

    private const string CacheFolderName = "Alchitex_Assets";
    private const string KeyNextCheckPrefix = "AssetUpdater_NextCheck_";

    // A failed check comes back at a tenth of its cooldown rather than waiting the full
    // term, so a bad connection at one launch doesn't cost days.
    private const int FailureBackoffDivisor = 10;

    // One connection at a time against someone else's CDN, with a gap between assets. Only
    // applies between assets that actually get fetched.
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly SemaphoreSlim _gate = new(1, 1);

    // =========================================================================
    // Reading
    // =========================================================================

    /// <summary>
    /// Where to read this asset from: the cached copy if one is present, the packaged copy
    /// otherwise. The only method a call site should ever use to build one of these paths.
    /// </summary>
    public static string Resolve(ManagedAsset asset)
    {
        try
        {
            var cacheFolder = CacheFolderPath;
            if (cacheFolder is null) return asset.PackagedPath;

            // Length as well as existence - an empty file is what a half-written one looks
            // like, and the packaged copy is a better answer than that.
            var cached = new FileInfo(Path.Combine(cacheFolder, asset.FileName));

            return cached.Exists && cached.Length > 0 ? cached.FullName : asset.PackagedPath;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AssetUpdater] Resolve('{asset.FileName}') failed, using the packaged copy: {ex.Message}");
            return asset.PackagedPath;
        }
    }

    // =========================================================================
    // Updating
    // =========================================================================

    /// <summary>Fire-and-forget, for startup. Never throws.</summary>
    public static void TriggerUpdate() => _ = TriggerUpdateAsync();

    /// <summary>
    /// Fetches every managed asset that is due, one at a time. Returns how many were
    /// replaced. Assets that aren't due cost nothing.
    /// </summary>
    public static async Task<int> TriggerUpdateAsync(CancellationToken cancellationToken = default)
    {
        // Nothing waits on this, so a second caller arriving mid-run leaves rather than
        // queues behind it.
        if (!await _gate.WaitAsync(0, CancellationToken.None)) return 0;

        try
        {
            var cacheFolder = EnsureCacheFolder();
            if (cacheFolder is null) return 0;

            var updated = 0;
            var fetchedAny = false;

            foreach (var asset in Managed)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!IsDue(asset)) continue;

                if (fetchedAny) await Task.Delay(RequestSpacing, cancellationToken);
                fetchedAny = true;

                var succeeded = await TryUpdateAsync(cacheFolder, asset, cancellationToken);

                Schedule(asset, succeeded);
                if (succeeded) updated++;
            }

            return updated;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AssetUpdater] Update run failed: {ex.Message}");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads one asset and swaps it in. Atomic by construction: Helpers.Download only
    /// reports success once the whole file is on disk, and the rename that follows is the
    /// only moment the cached copy changes.
    /// </summary>
    private static async Task<bool> TryUpdateAsync(string cacheFolder, ManagedAsset asset, CancellationToken cancellationToken)
    {
        string? downloaded = null;

        try
        {
            (var succeeded, downloaded) = await Helpers.Download(
                asset.RemoteUrl,
                cancellationToken,
                Helpers.UpdaterHttpClient,
                RequestTimeout,
                quiet: true);

            if (!succeeded || downloaded is null) return false;

            File.Move(downloaded, Path.Combine(cacheFolder, asset.FileName), overwrite: true);
            downloaded = null;

            Trace.WriteLine($"[AssetUpdater] '{asset.FileName}' updated.");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AssetUpdater] '{asset.FileName}' failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            // Only reachable if the rename failed - a generation run holding the old file
            // open is the likely cause. Don't leave the download behind for it to pile up.
            if (downloaded is not null)
            {
                try { File.Delete(downloaded); } catch { }
            }
        }
    }

    // =========================================================================
    // Schedule
    // =========================================================================
    //
    // Stored as the next time an asset may be checked rather than the last time it was, so
    // a success and a failure differ only in the value written.

    private static bool IsDue(ManagedAsset asset)
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[KeyNextCheckPrefix + asset.FileName] is not string stamp)
                return true;

            // RoundtripKind, or a UTC stamp parses back as local time and every comparison
            // below is off by the machine's offset.
            if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var next))
                return true;

            var wait = next - DateTime.UtcNow;

            // Further out than the cooldown itself means a clock that moved, or a corrupt
            // value; either way it would otherwise lock this asset out indefinitely.
            return wait <= TimeSpan.Zero || wait > asset.Cooldown;
        }
        catch
        {
            return true;
        }
    }

    private static void Schedule(ManagedAsset asset, bool succeeded)
    {
        try
        {
            var wait = succeeded ? asset.Cooldown : asset.Cooldown / FailureBackoffDivisor;

            ApplicationData.Current.LocalSettings.Values[KeyNextCheckPrefix + asset.FileName] =
                DateTime.UtcNow.Add(wait).ToString("O", CultureInfo.InvariantCulture);
        }
        catch { }
    }

    // =========================================================================
    // Storage
    // =========================================================================

    private static string? CacheFolderPath
    {
        get
        {
            try
            {
                return Path.Combine(ApplicationData.Current.LocalFolder.Path, CacheFolderName);
            }
            catch
            {
                return null;
            }
        }
    }

    private static string? EnsureCacheFolder()
    {
        try
        {
            var path = CacheFolderPath;
            if (path is null) return null;

            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AssetUpdater] Couldn't create the cache folder: {ex.Message}");
            return null;
        }
    }
}
