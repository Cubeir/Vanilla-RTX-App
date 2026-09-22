using System;
using System.IO;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

/// <summary>
/// The four data files RTX Reactor reads that are worth keeping current without shipping a new
/// build: the material table, the PBR blacklist and the two fallback zips. The mechanism is
/// <see cref="AssetUpdater"/>'s; this is only the manifest.
///
/// <para><b>Both endpoints are stated outright</b> rather than derived from a shared folder
/// constant. Deriving them quietly assumes every asset now and forever lives in one repo
/// directory, and it reads worse.</para>
///
/// <para><b>Cooldowns are set by how often each file actually changes</b>, which also
/// desynchronises them: after the first run they come due on different days, so a frequent user
/// rarely makes more than one of these requests per launch. materials.json is the one that gets
/// tuned, so it is checked daily; the zips change rarely enough to be measured in weeks.</para>
///
/// <para>TO ADD AN ASSET: one entry here, the file in <c>Assets/</c>, and the entry in
/// <see cref="All"/>. Read it through <see cref="AssetUpdater.Resolve"/>, never by building a
/// path to <c>Assets/</c> - the packaged copy is the fallback, not the thing that gets read,
/// and a path skips the cached copy entirely.</para>
/// </summary>
public static class AlchitexAssets
{
    private const string CacheFolder = "Alchitex_Assets";

    private static string Packaged(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Modules", "Alchitex", "Assets", fileName);

    private const string RemoteRoot =
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/";

    public static readonly ManagedAsset MaterialsJson = new(
        RemoteRoot + "materials.json",
        TimeSpan.FromDays(1),
        Packaged("materials.json"),
        CacheFolder);

    public static readonly ManagedAsset PbrBlacklistJson = new(
        RemoteRoot + "pbr_blacklist.json",
        TimeSpan.FromDays(4),
        Packaged("pbr_blacklist.json"),
        CacheFolder);

    public static readonly ManagedAsset FogZip = new(
        RemoteRoot + "vanilla-rtx-fog.zip",
        TimeSpan.FromDays(3),
        Packaged("vanilla-rtx-fog.zip"),
        CacheFolder);

    public static readonly ManagedAsset WaterFallbackZip = new(
        RemoteRoot + "water-fallback.zip",
        TimeSpan.FromDays(14),
        Packaged("water-fallback.zip"),
        CacheFolder);

    /// <summary>What the startup refresh walks. Order only decides who goes first when several are due at once.</summary>
    public static readonly ManagedAsset[] All =
    {
        MaterialsJson, PbrBlacklistJson, WaterFallbackZip, FogZip,
    };
}
