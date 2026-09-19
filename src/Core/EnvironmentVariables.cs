using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Windows.Storage;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// The app's own state: what is selected right now, what persists between sessions, and what
/// every persisted value falls back to.
///
/// <para><b>Three nested classes, and the split is the whole design.</b> <see cref="Defaults"/>
/// is the app's built-in answer and never changes at runtime. <see cref="Persistent"/> is what
/// the user has said instead, round-tripped through <c>LocalSettings</c> by reflection. <see
/// cref="Links"/> is the third case: a persisted value that is only usable if it still passes a
/// check, so every read goes through a validator and falls back to <see cref="Defaults"/> when
/// it doesn't.</para>
///
/// <para><b>A setting earns a <see cref="Links"/> accessor when a bad value would break a
/// feature rather than just look wrong.</b> Most settings can't be wrong - a slider is clamped,
/// a theme is one of three strings. An address can be: it survives a hard reset, an older build
/// may have written something this one no longer accepts, and <c>LocalSettings</c> is editable
/// by hand. Anything else that grows a validator later belongs here beside these.</para>
/// </summary>
public static class EnvironmentVariables
{
    private static readonly Windows.ApplicationModel.PackageVersion _version = App.GetPackageVersion();
    public static readonly string appVersion = $"{_version.Major}.{_version.Minor}.{_version.Build}.{_version.Revision}";
    public static readonly string appVersionMajor = $"{_version.Major}";
    public static readonly string appVersionMajorMinor = $"{_version.Major}.{_version.Minor}";
    public static readonly string appVersionMajorMinorBuild = $"{_version.Major}.{_version.Minor}.{_version.Build}";

    public static string VanillaRTXLocation = string.Empty;
    public static string VanillaRTXNormalsLocation = string.Empty;
    public static string VanillaRTXOpusLocation = string.Empty;

    public static string VanillaRTXVersion = string.Empty;
    public static string VanillaRTXNormalsVersion = string.Empty;
    public static string VanillaRTXOpusVersion = string.Empty;

    // Tied to checkboxes
    public static bool IsVanillaRTXEnabled = false;
    public static bool IsNormalsEnabled = false;
    public static bool IsOpusEnabled = false;

    public static ObservableCollection<(string Location, string Name, string Type, bool IsAlchitexCandidate)> SelectedPacks = new();

    public static class Persistent // These are saved and reloaded on app launch
    {
        public static bool IsTargetingPreview = Defaults.IsTargetingPreview;

        public static string? MinecraftInstallPath = null;
        public static string? MinecraftPreviewInstallPath = null;

        public static string? MinecraftDataPath = null;
        public static string? MinecraftPreviewDataPath = null;

        public static double FogMultiplier = Defaults.FogMultiplier;
        public static double EmissivityMultiplier = Defaults.EmissivityMultiplier;
        public static int NormalIntensity = Defaults.NormalIntensity;
        public static int MaterialNoiseOffset = Defaults.MaterialNoiseOffset;
        public static int RoughnessControlValue = Defaults.RoughnessControlValue;
        public static int LazifyNormalAlpha = Defaults.LazifyNormalAlpha;
        public static bool AddEmissivityAmbientLight = Defaults.AddEmissivityAmbientLight;

        public static string AppThemeMode = "Dark";
        public static bool SuspendUIAnimations = false;

        // The Launch button's options.txt edits, as MinecraftLauncher's name=value;name=value
        // form. A string rather than a collection because SaveSettings/LoadSettings round-trip
        // every field in here through LocalSettings via Convert.ChangeType, which only handles
        // primitives - see MinecraftLauncher.ParseOptions for the format and why an explicitly
        // empty configuration is stored as a marker instead of "".
        public static string LaunchOptions = Defaults.LaunchOptions;

        // Where the app goes for content it doesn't ship. Read these through Links, never
        // directly - that is what falls back to the built-in address when one has gone stale.
        public static string DocumentationUrl = Defaults.DocumentationUrl;
        public static string BugTrackerUrl = Defaults.BugTrackerUrl;
        public static string AnnouncementsUrl = Defaults.AnnouncementsUrl;
        public static string DlssProviderUrl = Defaults.DlssProviderUrl;
        public static string BetterRtxCreatorUrl = Defaults.BetterRtxCreatorUrl;
        public static string BetterRtxApiUrl = Defaults.BetterRtxApiUrl;
        public static string VanillaRtxRepository = Defaults.VanillaRtxRepository;
        public static string AlchitexMaterialsUrl = Defaults.AlchitexMaterialsUrl;
        public static string AlchitexBlacklistUrl = Defaults.AlchitexBlacklistUrl;
        public static string AlchitexFogUrl = Defaults.AlchitexFogUrl;
        public static string AlchitexWaterUrl = Defaults.AlchitexWaterUrl;
    }

    public static class Defaults // These are backed up to be used as a compass by other classes
    {
        public const bool IsTargetingPreview = false;
        public const double FogMultiplier = 1.0;
        public const double EmissivityMultiplier = 1.0;
        public const int NormalIntensity = 100;
        public const int MaterialNoiseOffset = 0;
        public const int RoughnessControlValue = 0;
        public const int LazifyNormalAlpha = 0;
        public const bool AddEmissivityAmbientLight = false;

        /// <summary>Serialized <see cref="Modules.MinecraftLauncher.DefaultOptions"/> - resolved once here so the stored form and the launcher's own defaults can never drift.</summary>
        public static readonly string LaunchOptions = Modules.MinecraftLauncher.SerializeOptions(Modules.MinecraftLauncher.DefaultOptions);

        // ── Addresses ────────────────────────────────────────────────────────
        // Each is spelled out in full rather than composed from a shared base, for the reason
        // AssetUpdater's own manifest states (CLAUDE.md 4.17): a base assumes every one of
        // these lives in the same place now and forever, and it reads worse.

        /// <summary>The markdown the titlebar's Help button renders.</summary>
        public const string DocumentationUrl = "https://github.com/Cubeir/Vanilla-RTX-App/blob/main/README.md";

        /// <summary>The markdown the titlebar's Bugs button renders.</summary>
        public const string BugTrackerUrl = "https://github.com/Cubeir/Minecraft-RTX-Bug-Tracking/blob/master/README.md";

        /// <summary>The markdown OnlineTexts parses into every in-app announcement.</summary>
        public const string AnnouncementsUrl = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/main/IN-APP-ANNOUNCEMENTS.md";

        /// <summary>Where the DLSS swapper's in-app browser goes to download runtimes.</summary>
        public const string DlssProviderUrl = "https://www.techpowerup.com/download/nvidia-dlss-dll";

        /// <summary>Where the BetterRTX manager's in-app browser goes to build a custom preset.</summary>
        public const string BetterRtxCreatorUrl = "https://bedrock.graphics/creator";

        /// <summary>The BetterRTX preset index. Its origin is also where individual presets are downloaded from - see <see cref="Links.BetterRtxPackDownload"/>.</summary>
        public const string BetterRtxApiUrl = "https://bedrock.graphics/api";

        /// <summary>
        /// The GitHub <c>owner/repo</c> the pack updater reads Vanilla RTX from - not a URL,
        /// because PackUpdater builds four different addresses out of it and only the repo
        /// itself is a meaningful choice. A repo pointed at here has to be laid out exactly
        /// like the original or the updater has nothing to find.
        /// </summary>
        public const string VanillaRtxRepository = "Cubeir/Vanilla-RTX";

        // Alchitex's online-updatable data assets (CLAUDE.md 4.17). The packaged copies are
        // always there; these are only ever a newer version of the same file.
        public const string AlchitexMaterialsUrl = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/materials.json";
        public const string AlchitexBlacklistUrl = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/pbr_blacklist.json";
        public const string AlchitexFogUrl = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/vanilla-rtx-fog.zip";
        public const string AlchitexWaterUrl = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/refs/heads/main/src/Modules/Alchitex/Assets/water-fallback.zip";
    }

    // =========================================================================
    //  Validated settings
    // =========================================================================

    /// <summary>What a stored address has to look like to be accepted. See <see cref="IsValidLink"/>.</summary>
    public enum LinkKind
    {
        /// <summary>Any http(s) address - it is handed to a browser or a WebView2, so the app never has to understand it.</summary>
        WebPage,

        /// <summary>http(s) naming a <c>.md</c> file, because it is fetched and parsed rather than displayed.</summary>
        Markdown,

        /// <summary>http(s) naming a <c>.json</c> file.</summary>
        Json,

        /// <summary>http(s) naming a <c>.zip</c> file.</summary>
        Zip,

        /// <summary>A GitHub <c>owner/repo</c> pair, not an address.</summary>
        GitHubRepository
    }

    /// <summary>
    /// Whether a stored value is usable for its purpose. Absolute http(s) for every address
    /// kind; the file kinds additionally have to name a file of that type, since the app
    /// fetches the bytes and parses them rather than displaying a page - handed an HTML page,
    /// Markdig renders the markup as literal text and the JSON readers degrade to their
    /// defaults, both of which read as a broken app rather than as a rejected setting.
    ///
    /// <para>The extension test looks at <see cref="Uri.AbsolutePath"/>, so a <c>#heading</c>
    /// fragment (which the markdown overlay honours by scrolling to it) doesn't disqualify a
    /// link.</para>
    /// </summary>
    public static bool IsValidLink(string? value, LinkKind kind)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();

        if (kind == LinkKind.GitHubRepository)
        {
            // owner/repo, nothing else. Rejecting a full URL here is deliberate: PackUpdater
            // builds raw.githubusercontent and codeload addresses out of these two segments,
            // and a pasted browser URL would silently produce four broken ones.
            var parts = trimmed.Split('/');
            return parts.Length == 2
                && parts.All(p => p.Length > 0 && p.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var extension = kind switch
        {
            LinkKind.Markdown => ".md",
            LinkKind.Json => ".json",
            LinkKind.Zip => ".zip",
            _ => null
        };

        return extension is null || uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="stored"/> if it still validates, <paramref name="fallback"/> otherwise.
    /// Never returns null or empty, so every call site can use the result directly.
    /// </summary>
    public static string ResolveLink(string? stored, string fallback, LinkKind kind)
        => IsValidLink(stored, kind) ? stored!.Trim() : fallback;

    /// <summary>The sentence a settings field shows when what was typed into it isn't accepted.</summary>
    public static string LinkRejectionReason(LinkKind kind) => kind switch
    {
        LinkKind.Markdown => "Needs to be a full http:// or https:// address ending in .md",
        LinkKind.Json => "Needs to be a full http:// or https:// address ending in .json",
        LinkKind.Zip => "Needs to be a full http:// or https:// address ending in .zip",
        LinkKind.GitHubRepository => "Needs to be a GitHub owner/repo pair, e.g. Cubeir/Vanilla-RTX",
        _ => "Needs to be a full http:// or https:// address"
    };

    /// <summary>
    /// Every address the app uses, resolved. <b>The packaged default is the guarantee and the
    /// stored value is the override</b> - the same shape <see cref="Modules.Alchitex.Core.AssetUpdater"/>
    /// uses for the assets themselves. A caller asks for a link and always gets a usable one,
    /// which is the only reason exposing these to the user is safe at all.
    ///
    /// <para>Nothing here reaches the network or judges what a link is worth. A value that
    /// parses is accepted; whether the page behind it is the real TechPowerUp is the user's
    /// business, which is the point of making them editable.</para>
    /// </summary>
    public static class Links
    {
        public static string Documentation => ResolveLink(Persistent.DocumentationUrl, Defaults.DocumentationUrl, LinkKind.Markdown);
        public static string BugTracker => ResolveLink(Persistent.BugTrackerUrl, Defaults.BugTrackerUrl, LinkKind.Markdown);
        public static string Announcements => ResolveLink(Persistent.AnnouncementsUrl, Defaults.AnnouncementsUrl, LinkKind.Markdown);
        public static string DlssProvider => ResolveLink(Persistent.DlssProviderUrl, Defaults.DlssProviderUrl, LinkKind.WebPage);
        public static string BetterRtxCreator => ResolveLink(Persistent.BetterRtxCreatorUrl, Defaults.BetterRtxCreatorUrl, LinkKind.WebPage);
        public static string BetterRtxApi => ResolveLink(Persistent.BetterRtxApiUrl, Defaults.BetterRtxApiUrl, LinkKind.WebPage);
        public static string VanillaRtxRepository => ResolveLink(Persistent.VanillaRtxRepository, Defaults.VanillaRtxRepository, LinkKind.GitHubRepository);
        public static string AlchitexMaterials => ResolveLink(Persistent.AlchitexMaterialsUrl, Defaults.AlchitexMaterialsUrl, LinkKind.Json);
        public static string AlchitexBlacklist => ResolveLink(Persistent.AlchitexBlacklistUrl, Defaults.AlchitexBlacklistUrl, LinkKind.Json);
        public static string AlchitexFog => ResolveLink(Persistent.AlchitexFogUrl, Defaults.AlchitexFogUrl, LinkKind.Zip);
        public static string AlchitexWater => ResolveLink(Persistent.AlchitexWaterUrl, Defaults.AlchitexWaterUrl, LinkKind.Zip);

        /// <summary>
        /// Where one BetterRTX preset's files are downloaded from, derived from
        /// <see cref="BetterRtxApi"/>'s own origin rather than spelled out separately.
        /// <b>The index and the downloads it lists have to come from the same host</b> - a
        /// repointed API whose downloads still went to bedrock.graphics would hand out uuids
        /// that host has never heard of.
        /// </summary>
        public static string BetterRtxPackDownload(string uuid)
        {
            var origin = Uri.TryCreate(BetterRtxApi, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : "https://bedrock.graphics";

            return $"{origin}/pack/{uuid}/release";
        }

        /// <summary>The raw file URL for a path inside <see cref="VanillaRtxRepository"/>'s master branch.</summary>
        public static string VanillaRtxRawFile(string pathInRepo)
            => $"https://raw.githubusercontent.com/{VanillaRtxRepository}/master/{pathInRepo}";

        /// <summary>The master-branch zipball of <see cref="VanillaRtxRepository"/>.</summary>
        public static string VanillaRtxZipball
            => $"https://github.com/{VanillaRtxRepository}/archive/refs/heads/master.zip";

        /// <summary>The human-facing page for <see cref="VanillaRtxRepository"/>, for a hint that links to it.</summary>
        public static string VanillaRtxRepositoryPage
            => $"https://github.com/{VanillaRtxRepository}";
    }

    // Window size defaults for all windows
    public const int WindowSizeX = 1150;
    public const int WindowSizeY = 620;
    public const int WindowMinSizeX = 950;
    public const int WindowMinSizeY = 615;

    // Saves persistent variables
    public static void SaveSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);

        foreach (var field in fields)
        {
            var value = field.GetValue(null);
            localSettings.Values[field.Name] = value;
        }
    }

    // Loads persitent variables
    public static void LoadSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var field in fields)
        {
            try
            {
                if (localSettings.Values.ContainsKey(field.Name))
                {
                    var savedValue = localSettings.Values[field.Name];
                    var convertedValue = Convert.ChangeType(savedValue, field.FieldType);
                    field.SetValue(null, convertedValue);
                }
            }
            catch
            {
                Trace.WriteLine($"[EnvironmentVariables] An issue occured loading settings");
            }
        }
    }
}
