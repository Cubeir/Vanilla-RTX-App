using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

/// <summary>
/// Keeps Alchitex's data assets current without shipping a new app build.
///
/// An MSIX package can't write to its own install folder, so the assets that ship with the
/// app are a guaranteed fallback rather than the thing that actually gets read. A newer copy
/// downloaded from the repo lives in LocalState\Alchitex_Assets, beside the caches
/// OnlineTexts, PackUpdater and the LUT manager already keep there.
///
/// Two entry points, and nothing else:
///
///   Resolve  - the path a caller should read. Returns the cached copy if there is one and
///              the packaged copy otherwise. Never waits, never fetches, never throws.
///   TriggerUpdate - fire-and-forget, called once at startup.
///
/// This is deliberately a nice-to-have. Every failure path ends in "use what we already
/// have", and the packaged asset is always there, so a user with no connection, a rate limit
/// or a captive portal gets exactly the behaviour they had before this existed.
///
/// The cooldown is long on purpose - the app already leans on GitHub for pack updates and
/// announcements, and this is an eventual, unhurried rollout rather than a way to push a
/// change out to everyone today.
///
/// TO ADD AN ASSET: put the file in Assets/ (and in the csproj - see §7 of CLAUDE.md), then
/// add its name to UpdatableAssets. Nothing else. Call sites that already go through Resolve
/// pick it up for free.
/// </summary>
public static class AlchitexAssets
{
    // Every updatable asset lives in this one folder in the repo, so the name is the only
    // thing that varies. An asset that ever needs to come from somewhere else is the point
    // at which this becomes a name/URL pair rather than a name.
    private const string RemoteFolder =
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/";

    private static readonly string[] UpdatableAssets =
    {
        "materials.json",
        "pbr_blacklist.json",
        "water-fallback.zip",
        "vanilla-rtx-fog.zip",
    };

    private const string CacheFolderName = "Alchitex_Assets";

    private const string KeyLastCheck = "AlchitexAssets_LastCheck";
    private const string KeyEtagPrefix = "AlchitexAssets_ETag_";

    // Stamped whether the run succeeded or failed, so a user who is offline at every launch
    // doesn't retry four requests on each one.
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(120);

    // Sequential with a gap rather than four at once: these are small files and there is no
    // hurry, and it keeps us to one connection at a time against someone else's CDN.
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly SemaphoreSlim _gate = new(1, 1);

    // =========================================================================
    // Resolve
    // =========================================================================

    /// <summary>
    /// Where to read <paramref name="fileName"/> from: the cached online copy if one is
    /// present, the packaged copy otherwise.
    ///
    /// Safe for any asset name - one that isn't updatable simply resolves to the packaged
    /// path - so call sites can use this uniformly rather than deciding per file which ones
    /// have an online version.
    /// </summary>
    public static string Resolve(string packagedAssetsRoot, string fileName)
    {
        var packaged = Path.Combine(packagedAssetsRoot, fileName);

        try
        {
            if (!UpdatableAssets.Contains(fileName, StringComparer.OrdinalIgnoreCase)) return packaged;

            var cacheFolder = CacheFolderPath;
            if (cacheFolder is null) return packaged;

            // Length as well as existence: an empty file is what a half-written one looks
            // like, and the packaged copy is a better answer than that.
            var cached = new FileInfo(Path.Combine(cacheFolder, fileName));

            return cached.Exists && cached.Length > 0 ? cached.FullName : packaged;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AlchitexAssets] Resolve('{fileName}') failed, using the packaged copy: {ex.Message}");
            return packaged;
        }
    }

    // =========================================================================
    // Update
    // =========================================================================

    /// <summary>Fire-and-forget update, for startup. Never throws.</summary>
    public static void TriggerUpdate() => _ = TriggerUpdateAsync();

    /// <summary>
    /// Refreshes every updatable asset, one at a time, if the cooldown has expired.
    /// Returns the number of assets actually rewritten.
    /// </summary>
    public static async Task<int> TriggerUpdateAsync()
    {
        try
        {
            if (!IsCooldownExpired()) return 0;

            // Nothing waits on this, so a second caller arriving mid-run should leave rather
            // than queue behind it.
            if (!await _gate.WaitAsync(0)) return 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AlchitexAssets] Couldn't start an update: {ex.Message}");
            return 0;
        }

        try
        {
            var cacheFolder = EnsureCacheFolder();
            if (cacheFolder is null) return 0;

            var updated = 0;

            for (var i = 0; i < UpdatableAssets.Length; i++)
            {
                if (i > 0) await Task.Delay(RequestSpacing);

                if (await TryUpdateAsync(cacheFolder, UpdatableAssets[i])) updated++;
            }

            // Stamped even when nothing came back. A failed check costs the same cooldown as
            // a successful one - see the class remarks.
            StampCheck();

            Trace.WriteLine($"[AlchitexAssets] Update finished, {updated} asset(s) replaced.");
            return updated;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AlchitexAssets] Update failed: {ex.Message}");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<bool> TryUpdateAsync(string cacheFolder, string fileName)
    {
        var destination = Path.Combine(cacheFolder, fileName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RemoteFolder + fileName);

            // These bytes are identical nearly every time we ask, so ask conditionally and
            // let the server answer 304 instead of resending a megabyte.
            var etag = ReadEtag(fileName);
            if (etag is not null && File.Exists(destination))
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var cts = new CancellationTokenSource(RequestTimeout);
            using var response = await Helpers.UpdaterHttpClient.SendAsync(request, cts.Token);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                Trace.WriteLine($"[AlchitexAssets] '{fileName}' unchanged.");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"[AlchitexAssets] '{fileName}': HTTP {(int)response.StatusCode}.");
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);

            // A rate-limit page, a captive portal's login form and a truncated download are
            // all "successful" responses. Replacing materials.json with any of them would
            // silently drop the whole pack to default PBR, which is exactly the kind of
            // failure nobody would report as a bug - so nothing is written until it parses.
            if (!IsPlausible(fileName, bytes))
            {
                Trace.WriteLine($"[AlchitexAssets] '{fileName}': {bytes.Length} bytes that don't parse - keeping what we have.");
                return false;
            }

            WriteAtomically(destination, bytes);
            WriteEtag(fileName, response.Headers.ETag?.Tag);

            Trace.WriteLine($"[AlchitexAssets] '{fileName}' updated ({bytes.Length} bytes).");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AlchitexAssets] '{fileName}' failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether the bytes are the kind of file we asked for. Not a checksum - just enough to
    /// reject anything that plainly isn't the asset.
    /// </summary>
    private static bool IsPlausible(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0) return false;

        try
        {
            if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                // Same leniency the real loaders use, so we can't reject a file they would
                // have accepted - materials.json is allowed to carry comments.
                using var _ = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                return true;
            }

            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new MemoryStream(bytes);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

                return archive.Entries.Count > 0;
            }
        }
        catch
        {
            return false;
        }

        // Some other kind of file: a non-empty response is the only claim we can make.
        return true;
    }

    /// <summary>
    /// Writes beside the target and renames over it, so a torn download can never be left
    /// where Resolve would hand it to a caller.
    /// </summary>
    private static void WriteAtomically(string destination, byte[] bytes)
    {
        var incoming = destination + ".incoming";

        try
        {
            File.WriteAllBytes(incoming, bytes);
            File.Move(incoming, destination, overwrite: true);
        }
        finally
        {
            // Only reachable if the move failed - a generation run holding the old file open
            // is the likely cause, and the next check will try again.
            if (File.Exists(incoming))
            {
                try { File.Delete(incoming); } catch { }
            }
        }
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
            Trace.WriteLine($"[AlchitexAssets] Couldn't create the cache folder: {ex.Message}");
            return null;
        }
    }

    // One settings key per asset rather than a serialized dictionary - it keeps this off the
    // reflection-based JSON path a trimmed Release build breaks (see §6 of CLAUDE.md).
    private static string? ReadEtag(string fileName)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values[KeyEtagPrefix + fileName] as string;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteEtag(string fileName, string? etag)
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            var key = KeyEtagPrefix + fileName;

            if (string.IsNullOrEmpty(etag)) values.Remove(key);
            else values[key] = etag;
        }
        catch { }
    }

    private static void StampCheck()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[KeyLastCheck] = DateTime.UtcNow.ToString("O");
        }
        catch { }
    }

    private static bool IsCooldownExpired()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[KeyLastCheck] is not string stamp) return true;
            if (!DateTime.TryParse(stamp, out var last)) return true;

            var age = DateTime.UtcNow - last;

            // A clock that moved backwards would otherwise lock updates out for days.
            if (age < TimeSpan.Zero)
            {
                try { ApplicationData.Current.LocalSettings.Values.Remove(KeyLastCheck); } catch { }
                return true;
            }

            return age >= Cooldown;
        }
        catch
        {
            return true;
        }
    }
}
