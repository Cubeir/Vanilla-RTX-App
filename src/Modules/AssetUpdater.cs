using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules;

/// <summary>Where the bytes a read handed back came from.</summary>
public enum AssetSource
{
    /// <summary>Just downloaded, and now the cached copy.</summary>
    Fetched,

    /// <summary>The cached copy - either still inside its cooldown, confirmed current by a 304, or all a failed fetch left us.</summary>
    Cache,

    /// <summary>The copy that shipped with the app. Only possible for an asset that has one.</summary>
    Packaged,

    /// <summary>Nothing to read: no cache, no packaged copy, and the fetch didn't work.</summary>
    Unavailable
}

/// <param name="Path">The file to read, or null when <paramref name="Source"/> is Unavailable.</param>
/// <param name="CheckedRemote">
/// The remote answered just now - either with content or with a 304 saying the local copy is
/// current. <see cref="AssetSource"/> alone cannot say this: a 304 and a cooldown both report
/// <see cref="AssetSource.Cache"/>, and once conditional requests are in play the 304 is the
/// ordinary case. A failed attempt is false, so a caller showing "just checked" leaves the way
/// open to try again.
/// </param>
public readonly record struct AssetRead(string? Path, AssetSource Source, bool CheckedRemote = false);

/// <summary>Whether a fetch may be made to wait behind others going to the same host.</summary>
public enum FetchPriority
{
    /// <summary>Someone is looking at the result. Goes out immediately.</summary>
    Foreground,

    /// <summary>Nobody asked for this. Spaced out behind whatever else is going to that host.</summary>
    Background
}

/// <summary>
/// One remote file the app keeps a local copy of: where it comes from, how often that is worth
/// re-asking, and - for an asset the app also ships - where the built-in copy lives.
///
/// <para>Everything but the first two has a usable default, so an entry states only what is
/// unusual about it.</para>
/// </summary>
/// <param name="RemoteUrl">The address, which is also the identity: the cooldown, the stored ETag and the default cache file name all key off it.</param>
/// <param name="Cooldown">How long a local copy is used without asking the remote anything.</param>
/// <param name="PackagedPath">The copy shipped inside the app, if there is one. It is the guarantee that a read always has an answer.</param>
/// <param name="CacheFolderName">A subfolder of LocalState to keep the cached copy in. Null puts it in LocalState itself.</param>
/// <param name="FileName">The cached copy's file name. Defaults to the packaged file's name, or to a hash of the URL when there is no packaged copy.</param>
/// <param name="FailureBackoff">How long to wait after a failed check instead of the full cooldown. Defaults to a tenth of it, so a bad connection at one launch doesn't cost days.</param>
/// <param name="Timeout">Deadline for one attempt when there is nothing to fall back on.</param>
/// <param name="TimeoutWhenCached">Deadline for one attempt when a local copy already exists - with something to show, a slow network isn't worth waiting out. Defaults to <paramref name="Timeout"/>.</param>
public sealed record ManagedAsset(
    string RemoteUrl,
    TimeSpan Cooldown,
    string? PackagedPath = null,
    string? CacheFolderName = null,
    string? FileName = null,
    TimeSpan? FailureBackoff = null,
    TimeSpan? Timeout = null,
    TimeSpan? TimeoutWhenCached = null)
{
    public string CacheFileName => FileName
        ?? (PackagedPath is not null ? Path.GetFileName(PackagedPath) : AssetUpdater.DefaultFileName(RemoteUrl));

    public TimeSpan ResolvedFailureBackoff => FailureBackoff ?? TimeSpan.FromTicks(Cooldown.Ticks / 10);
    public TimeSpan ResolvedTimeout => Timeout ?? TimeSpan.FromSeconds(30);
    public TimeSpan ResolvedTimeoutWhenCached => TimeoutWhenCached ?? ResolvedTimeout;
}

/// <summary>
/// The app's one way of keeping a remote file locally: announcements, the documentation pages,
/// and RTX Reactor's data assets all go through here, and anything added later should too.
///
/// <para><b>Three entry points, and the difference between the first two is the whole design:</b>
/// <list type="bullet">
///   <item><see cref="Resolve"/> - what is on disk right now. Cached copy, else packaged copy,
///   else null. Never waits, never fetches, never throws. For a caller that needs an answer
///   this instant and has no business blocking on a network.</item>
///   <item><see cref="ResolveFreshOrCachedAsync"/> - the remote first, when the cooldown has
///   passed, and the local copy when it hasn't or when the fetch failed. For a caller that can
///   wait a moment and wants the newest thing available.</item>
///   <item><see cref="TriggerUpdate"/> - refreshes a set of assets in the background and hands
///   nothing back. For startup, where nobody is waiting and nobody reads the result.</item>
/// </list></para>
///
/// <para><b>The cooldown is skipped when there is nothing to fall back on.</b> An asset with a
/// packaged copy can afford to settle for what it has and try again in a few days; one with
/// neither a cache nor a packaged copy has nothing to show, so the first read tries whatever the
/// schedule says.</para>
///
/// <para><b>Every failure ends in "use what we already have".</b> Offline, rate limited or behind
/// a captive portal, a caller gets the same answer it got before any of this existed - see
/// <see cref="AssetSource"/> for how it can tell which.</para>
///
/// <para>Fetching goes through <see cref="Helpers.Download"/>, so retries, the 4xx rule, the
/// stall deadline and conditional requests are shared with every other download in the app.</para>
/// </summary>
public static class AssetUpdater
{
    // ── Request pacing ───────────────────────────────────────────────────────
    //
    // One connection at a time to a host, and a gap between background requests to it, so a
    // launch that has several things to refresh trickles rather than bursts. Only the *start*
    // of a request is serialised - a large download must never hold a document fetch up behind
    // it, which is also why priority exists at all.

    private static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(666);

    private sealed class HostGate
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public DateTime NextAllowed = DateTime.MinValue;
    }

    private static readonly ConcurrentDictionary<string, HostGate> _gates = new(StringComparer.OrdinalIgnoreCase);

    private static async Task TakeTicketAsync(string url, FetchPriority priority, CancellationToken cancellationToken)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        var gate = _gates.GetOrAdd(host, _ => new HostGate());

        await gate.Lock.WaitAsync(cancellationToken);
        try
        {
            if (priority == FetchPriority.Background)
            {
                var wait = gate.NextAllowed - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            }

            gate.NextAllowed = DateTime.UtcNow + RequestSpacing;
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The best local copy of an asset: the cached one if it is there, the packaged one
    /// otherwise, null if the asset has neither. Never waits and never fetches, so a caller in
    /// the middle of something (a generation run reading materials.json) is never held up.
    ///
    /// <para><b>A Debug build prefers the packaged copy</b> for an asset that has one. One
    /// successful check is otherwise enough for the cache to win every read from then on, so an
    /// edit to a bundled asset appears to do nothing until the remote catches up - the wrong
    /// loop for the file that gets edited most (materials.json, via MaterialsBootstrapper). This
    /// is the only behavioural difference between the two configurations.</para>
    /// </summary>
    public static string? Resolve(ManagedAsset asset)
    {
#if DEBUG
        if (asset.PackagedPath is not null) return asset.PackagedPath;
#endif
        var cached = CachedFilePath(asset);

        // Length as well as existence - an empty file is what a half-written one looks like,
        // and the packaged copy is a better answer than that.
        if (cached is not null && File.Exists(cached) && new FileInfo(cached).Length > 0)
            return cached;

        return asset.PackagedPath;
    }

    /// <summary>
    /// The newest copy that can be had without making the caller wait long: the remote when the
    /// cooldown has passed, the local copy otherwise. <paramref name="force"/> ignores the
    /// cooldown outright, which is what a Reload button asks for.
    ///
    /// <para>Fetching, and therefore this method, is the only thing that ever writes the cache.
    /// The returned <see cref="AssetRead.Source"/> says where the bytes came from, which is how
    /// a caller can tell a fresh page from a cached one without asking a second question.</para>
    /// </summary>
    public static async Task<AssetRead> ResolveFreshOrCachedAsync(
        ManagedAsset asset,
        bool force = false,
        FetchPriority priority = FetchPriority.Foreground,
        CancellationToken cancellationToken = default)
    {
        var cachePath = CachedFilePath(asset);
        var haveCache = cachePath is not null && File.Exists(cachePath) && new FileInfo(cachePath).Length > 0;

        // Nothing local at all means the schedule cannot be honoured: there would be nothing to
        // hand back. An asset with a packaged copy is never in that position.
        var mustTry = !haveCache && asset.PackagedPath is null;

        if (!force && !mustTry && !IsDue(asset))
            return Local(asset, cachePath, haveCache);

        var cacheFolder = EnsureFolderFor(asset);
        if (cacheFolder is null) return Local(asset, cachePath, haveCache);

        string? downloaded = null;
        try
        {
            await TakeTicketAsync(asset.RemoteUrl, priority, cancellationToken);

            var result = await Helpers.Download(
                asset.RemoteUrl,
                cancellationToken,
                Helpers.UpdaterHttpClient,
                haveCache ? asset.ResolvedTimeoutWhenCached : asset.ResolvedTimeout,
                quiet: true,
                // Only ever conditional against a cached copy that is actually present - a
                // validator without its file answers 304 for content nobody has.
                conditional: haveCache);

            if (result.NotModified)
            {
                Trace.WriteLine($"[AssetUpdater] '{asset.CacheFileName}' unchanged (304).");
                Schedule(asset, succeeded: true);
                TouchCache(cachePath);
                return new AssetRead(cachePath, AssetSource.Cache, CheckedRemote: true);
            }

            downloaded = result.Path;
            if (!result.Success || downloaded is null)
            {
                Schedule(asset, succeeded: false);
                return Local(asset, cachePath, haveCache);
            }

            var destination = Path.Combine(cacheFolder, asset.CacheFileName);

            // The destination name comes from the asset, never from what came back:
            // Helpers.Download uniquifies around anything already in its folder, so this can
            // arrive as materials-1.json and still has to land as materials.json.
            File.Move(downloaded, destination, overwrite: true);
            downloaded = null;

            // After the move, never before it: a validator that outlives the file it describes
            // makes the next request answer 304 for content nobody has, and the cache then sits
            // a version behind until the remote file changes again.
            Helpers.SetValidator(asset.RemoteUrl, result.ETag);
            Schedule(asset, succeeded: true);

            Trace.WriteLine($"[AssetUpdater] '{asset.CacheFileName}' updated.");
            return new AssetRead(destination, AssetSource.Fetched, CheckedRemote: true);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up on us; its own cancellation is the answer, not a failed check.
            return Local(asset, cachePath, haveCache);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AssetUpdater] '{asset.CacheFileName}' failed: {ex.GetType().Name}: {ex.Message}");
            Schedule(asset, succeeded: false);
            return Local(asset, cachePath, haveCache);
        }
        finally
        {
            // Only reachable if the move failed - something holding the old file open is the
            // likely cause. Don't leave the download behind for it to pile up.
            if (downloaded is not null)
            {
                try { File.Delete(downloaded); } catch { }
            }
        }
    }

    /// <summary>
    /// Refreshes whichever of <paramref name="assets"/> are due, one at a time, in the
    /// background. Fire-and-forget: nothing waits on it, nothing reads its result, and an asset
    /// that isn't due costs nothing. Callers read through <see cref="Resolve"/> and take
    /// whatever is there at the time.
    /// </summary>
    public static void TriggerUpdate(params ManagedAsset[] assets) => _ = TriggerUpdateAsync(assets);

    /// <inheritdoc cref="TriggerUpdate"/>
    public static async Task<int> TriggerUpdateAsync(ManagedAsset[] assets, CancellationToken cancellationToken = default)
    {
        var updated = 0;

        foreach (var asset in assets)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!IsDue(asset)) continue;

            var read = await ResolveFreshOrCachedAsync(asset, force: false, FetchPriority.Background, cancellationToken);
            if (read.Source == AssetSource.Fetched) updated++;
        }

        return updated;
    }

    /// <summary>The local answer, whatever it is - used by every path that didn't get a fresh copy.</summary>
    private static AssetRead Local(ManagedAsset asset, string? cachePath, bool haveCache)
    {
        if (haveCache) return new AssetRead(cachePath, AssetSource.Cache);
        if (asset.PackagedPath is not null) return new AssetRead(asset.PackagedPath, AssetSource.Packaged);
        return new AssetRead(null, AssetSource.Unavailable);
    }

    // ── Schedule ─────────────────────────────────────────────────────────────
    //
    // Stored as the next time an asset may be checked rather than the last time it was, so a
    // success and a failure differ only in the value written. A 304 counts as a success: the
    // cached copy was confirmed current, which is exactly what the check is for.

    private const string KeyNextCheckPrefix = "AssetUpdater_NextCheck_";

    private static bool IsDue(ManagedAsset asset)
    {
        var stamp = ReadSetting(ScheduleKey(asset));
        if (stamp is null) return true;

        // RoundtripKind, or a UTC stamp written with "O" parses back as *local* time and every
        // comparison below is off by the machine's offset: west of UTC each check reads as older
        // than the cooldown, east of it as being in the future. Either way the cooldown never
        // holds and the asset is refetched on every launch.
        if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var next))
            return true;

        var wait = next - DateTime.UtcNow;

        // Further out than the cooldown itself means a clock that moved, or a corrupt value;
        // either way it would otherwise lock this asset out indefinitely.
        return wait <= TimeSpan.Zero || wait > asset.Cooldown;
    }

    private static void Schedule(ManagedAsset asset, bool succeeded)
    {
        var wait = succeeded ? asset.Cooldown : asset.ResolvedFailureBackoff;
        WriteSetting(ScheduleKey(asset), DateTime.UtcNow.Add(wait).ToString("O", CultureInfo.InvariantCulture));
    }

    private static string ScheduleKey(ManagedAsset asset) =>
        KeyNextCheckPrefix + DefaultFileName(asset.RemoteUrl);

    // ── Storage ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A stable, short file name for an address, used for the cache file of an asset that ships
    /// no packaged copy and for every schedule key. The first 8 bytes of the URL's SHA-256, so
    /// two documents at different addresses never share an entry and a user who points Help at
    /// their own page gets their own.
    /// </summary>
    internal static string DefaultFileName(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)), 0, 8).ToLowerInvariant();

    private static string? FolderFor(ManagedAsset asset)
    {
        var root = Helpers.LocalStateFolder;
        if (root is null) return null;

        return asset.CacheFolderName is null ? root : Path.Combine(root, asset.CacheFolderName);
    }

    private static string? CachedFilePath(ManagedAsset asset)
    {
        var folder = FolderFor(asset);
        return folder is null ? null : Path.Combine(folder, asset.CacheFileName);
    }

    private static string? EnsureFolderFor(ManagedAsset asset)
    {
        try
        {
            var folder = FolderFor(asset);
            if (folder is null) return null;

            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AssetUpdater] Couldn't create the cache folder: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Re-stamps a cached file that a 304 confirmed is current. The schedule is what the
    /// cooldown actually reads, so this only keeps the file's own age honest for anyone
    /// looking at the folder.
    /// </summary>
    private static void TouchCache(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch { }
    }

    // LocalSettings, with the same in-memory fallback the validator store uses, so this works -
    // and can be tested - in a host without packaged app data.
    private static readonly Dictionary<string, string> _settingsFallback = new(StringComparer.Ordinal);

    private static string? ReadSetting(string key)
    {
        try { return ApplicationData.Current.LocalSettings.Values[key] as string; }
        catch { lock (_settingsFallback) return _settingsFallback.TryGetValue(key, out var v) ? v : null; }
    }

    private static void WriteSetting(string key, string value)
    {
        try { ApplicationData.Current.LocalSettings.Values[key] = value; }
        catch { lock (_settingsFallback) _settingsFallback[key] = value; }
    }
}
