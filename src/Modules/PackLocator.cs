using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vanilla_RTX_App.Modules;

/// Scans the user's Minecraft resource pack folders (both regular and dev — see
/// <see cref="GetOrderedScanPaths"/> for scan order and priority) for installed
/// Vanilla RTX, Vanilla RTX Normals, and Vanilla RTX Opus packs...
public class PackLocator
{
    public const string VANILLA_RTX_HEADER_UUID = "a5c3cc7d-1740-4b5e-ae2c-71bc14b3f63b";
    public const string VANILLA_RTX_MODULE_UUID = "af805084-fafa-4124-9ae2-00be4bc202dc";
    public const string VANILLA_RTX_NORMALS_HEADER_UUID = "bbe2b225-b45b-41c2-bd3b-465cd83e6071";
    public const string VANILLA_RTX_NORMALS_MODULE_UUID = "b2eef2c6-d893-467e-b31d-cda7bf643eaa";
    public const string VANILLA_RTX_OPUS_HEADER_UUID = "7c87f859-4d79-4d51-8887-bf450b2b2bfa";
    public const string VANILLA_RTX_OPUS_MODULE_UUID = "be0b22f0-ad13-4bbd-81ba-b457fd9e38b8";

    // Change the minimum version of Vanilla RTX packs detected by the app
    private static readonly int[] MinVersion = [1, 0, 0];

    public static string LocatePacks(bool isTargetingPreview,
        out string vanillaRTXLocation, out string vanillaRTXVersion,
        out string vanillaRTXNormalsLocation, out string vanillaRTXNormalsVersion,
        out string vanillaRTXOpusLocation, out string vanillaRTXOpusVersion)
    {
        vanillaRTXLocation = string.Empty;
        vanillaRTXVersion = string.Empty;
        vanillaRTXNormalsLocation = string.Empty;
        vanillaRTXNormalsVersion = string.Empty;
        vanillaRTXOpusLocation = string.Empty;
        vanillaRTXOpusVersion = string.Empty;

        try
        {
            var versionName = MinecraftUserDataLocator.GetVersionDisplayName(isTargetingPreview);

            if (!MinecraftUserDataLocator.IsDataValid(isTargetingPreview))
                return $"❌ {versionName} data folder not found, is the correct version installed?";

            var allManifestFiles = new List<string>();

            foreach (var scanPath in GetOrderedScanPaths(isTargetingPreview))
            {
                allManifestFiles.AddRange(
                    Directory.GetFiles(scanPath, "manifest.json", SearchOption.AllDirectories)
                );
            }

            if (allManifestFiles.Count == 0)
                return $"❌ Resource pack directory not found, is the correct version of {versionName} installed?";

            // Track latest version for each pack type
            (string path, int[] version)? latestVanillaRTX = null;
            (string path, int[] version)? latestVanillaRTXNormals = null;
            (string path, int[] version)? latestVanillaRTXOpus = null;

            var results = new List<string>();

            static int CompareVersion(int[] a, int[] b)
            {
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    int va = i < a.Length ? a[i] : 0;
                    int vb = i < b.Length ? b[i] : 0;
                    if (va > vb) return 1;
                    if (va < vb) return -1;
                }
                return 0;
            }

            foreach (var file in allManifestFiles)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var data = ParseManifestJson(json);
                    if (data == null)
                    {
                        results.Add("⚠️ Detected a package with a malformed manifest (likely from a third-party, you can ignore this warning).");
                        continue;
                    }

                    var version = ParseTripletVersion(data["header"]?["version"]);
                    if (version == null)
                        continue; // not a triplet version → definitively not one of our tracked packs

                    if (CompareVersion(version, MinVersion) < 0)
                        continue;

                    string? headerUUID = data["header"]?["uuid"]?.ToString();
                    string folder = Path.GetDirectoryName(file)!;

                    string? moduleUUID = null;
                    if (data["modules"] is JArray modules && modules.Count > 0)
                        moduleUUID = modules[0]["uuid"]?.ToString();

                    if (string.Equals(headerUUID, VANILLA_RTX_HEADER_UUID, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(moduleUUID, VANILLA_RTX_MODULE_UUID, StringComparison.OrdinalIgnoreCase))
                    {
                        if (latestVanillaRTX == null || CompareVersion(version, latestVanillaRTX.Value.version) > 0)
                            latestVanillaRTX = (folder, version);
                    }
                    else if (string.Equals(headerUUID, VANILLA_RTX_NORMALS_HEADER_UUID, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(moduleUUID, VANILLA_RTX_NORMALS_MODULE_UUID, StringComparison.OrdinalIgnoreCase))
                    {
                        if (latestVanillaRTXNormals == null || CompareVersion(version, latestVanillaRTXNormals.Value.version) > 0)
                            latestVanillaRTXNormals = (folder, version);
                    }
                    else if (string.Equals(headerUUID, VANILLA_RTX_OPUS_HEADER_UUID, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(moduleUUID, VANILLA_RTX_OPUS_MODULE_UUID, StringComparison.OrdinalIgnoreCase))
                    {
                        if (latestVanillaRTXOpus == null || CompareVersion(version, latestVanillaRTXOpus.Value.version) > 0)
                            latestVanillaRTXOpus = (folder, version);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[PACK_LOCATOR] Parsed a malformed manifest.json, reason:\n{ex.ToString}\nat file: {file}");
                    results.Add("⚠️ Detected a package with a malformed manifest (likely from a third-party, you can ignore this warning).");
                }
            }

            // Set out parameters and results
            if (latestVanillaRTX != null)
            {
                vanillaRTXLocation = latestVanillaRTX.Value.path;
                vanillaRTXVersion = $"{latestVanillaRTX.Value.version[0]}.{latestVanillaRTX.Value.version[1]}.{latestVanillaRTX.Value.version[2]}";
                results.Add($"✅ Installed: Vanilla RTX - {vanillaRTXVersion}");
            }
            else
            {
                results.Add("⚠️ Not installed: Vanilla RTX");
            }

            if (latestVanillaRTXNormals != null)
            {
                vanillaRTXNormalsLocation = latestVanillaRTXNormals.Value.path;
                vanillaRTXNormalsVersion = $"{latestVanillaRTXNormals.Value.version[0]}.{latestVanillaRTXNormals.Value.version[1]}.{latestVanillaRTXNormals.Value.version[2]}";
                results.Add($"✅ Installed: Vanilla RTX Normals - {vanillaRTXNormalsVersion}");
            }
            else
            {
                results.Add("⚠️ Not installed: Vanilla RTX Normals");
            }

            if (latestVanillaRTXOpus != null)
            {
                vanillaRTXOpusLocation = latestVanillaRTXOpus.Value.path;
                vanillaRTXOpusVersion = $"{latestVanillaRTXOpus.Value.version[0]}.{latestVanillaRTXOpus.Value.version[1]}.{latestVanillaRTXOpus.Value.version[2]}";
                results.Add($"✅ Installed: Vanilla RTX Opus - {vanillaRTXOpusVersion}");
            }
            else
            {
                results.Add("⚠️ Not installed: Vanilla RTX Opus");
            }

            return string.Join(Environment.NewLine, results);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }


    private static JObject? ParseManifestJson(string json)
    {
        try
        {
            using var sr = new StringReader(json);
            using var reader = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None };
            var loadSettings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
            return JObject.Load(reader, loadSettings);
        }
        catch
        {
            try { return JObject.Parse(json); }
            catch { return null; }
        }
    }
    /// Our three tracked packs always ship version as a triplet int array, e.g. [1, 0, 6].
    /// Anything else (SemVer but string, malformed, missing) can't be one of ours, returns
    /// null so the caller skips the entry without throwing.
    private static int[]? ParseTripletVersion(JToken? versionToken)
    {
        if (versionToken is not JArray arr || arr.Count != 3)
            return null;

        var result = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (arr[i].Type != JTokenType.Integer)
                return null;
            result[i] = (int)arr[i];
        }
        return result;
    }


    /// <summary>
    /// Resource pack folders to scan, in priority order. Dev is scanned first so that when the
    /// same pack/version exists in both locations, the tie-break in LocatePacks treats the dev
    /// copy as authoritative (assumption: dev packs take priority over regular ones at runtime,
    /// Test and change later if wrong
    /// </summary>
    private static IEnumerable<string> GetOrderedScanPaths(bool isTargetingPreview)
    {
        var dev = MinecraftUserDataLocator.GetResourcePacksPath(isTargetingPreview, development: true);
        if (!string.IsNullOrEmpty(dev) && Directory.Exists(dev))
            yield return dev;

        var rp = MinecraftUserDataLocator.GetResourcePacksPath(isTargetingPreview, development: false);
        if (!string.IsNullOrEmpty(rp) && Directory.Exists(rp))
            yield return rp;
    }
}
