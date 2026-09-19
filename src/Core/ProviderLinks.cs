using System;
using Vanilla_RTX_App.Modules;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// The four addresses the app goes to for someone else's content, and the rule for overriding
/// them. Two are pages a feature browses (where DLSS runtimes come from, where BetterRTX presets
/// are built); two are the markdown documents the Help and Bug buttons render.
///
/// <para><b>The packaged default is the guarantee, the stored value is the override</b> - the
/// same shape <see cref="Modules.Alchitex.Core.AssetUpdater"/> uses for its data assets. A
/// caller asks for a link and always gets a usable one: the user's, if it still passes the
/// validation that accepted it, and the built-in otherwise. That second half is what makes this
/// safe to expose at all. A stored URL can go stale in ways the settings panel never sees - a
/// hard reset clears it, an older build wrote a value this one no longer accepts, a hand-edited
/// LocalSettings - and the alternative to falling back is a feature that silently navigates
/// nowhere.</para>
///
/// <para><b>Nothing here reaches the network or decides what a link is worth.</b> A URL that
/// parses is accepted; whether the page behind it is the real TechPowerUp or a useful document
/// is the user's business, which is the point of making them editable.</para>
/// </summary>
public static class ProviderLinks
{
    /// <summary>Where the DLSS swapper's in-app browser goes to download runtimes.</summary>
    public const string DefaultDlssProvider = "https://www.techpowerup.com/download/nvidia-dlss-dll";

    /// <summary>Where the BetterRTX manager's in-app browser goes to build a custom preset.</summary>
    public const string DefaultBetterRtxProvider = "https://bedrock.graphics/creator";

    /// <summary>The markdown the Help button renders.</summary>
    public const string DefaultDocumentation = "https://github.com/Cubeir/Vanilla-RTX-App/blob/main/README.md";

    /// <summary>The markdown the Bug button renders.</summary>
    public const string DefaultBugTracker = "https://github.com/Cubeir/Minecraft-RTX-Bug-Tracking/blob/master/README.md";

    /// <summary>What a given link has to look like to be accepted. See <see cref="IsValid"/>.</summary>
    public enum LinkKind
    {
        /// <summary>Any http(s) address - it is opened in a browser or a WebView2, so the app never has to understand it.</summary>
        WebPage,

        /// <summary>An http(s) address pointing at a <c>.md</c> file, because <see cref="Overlays.MarkdownOverlay"/> fetches and parses it rather than displaying a page.</summary>
        Markdown
    }

    public static string DlssProvider => Resolve(EnvironmentVariables.Persistent.DlssProviderUrl, DefaultDlssProvider, LinkKind.WebPage);
    public static string BetterRtxProvider => Resolve(EnvironmentVariables.Persistent.BetterRtxProviderUrl, DefaultBetterRtxProvider, LinkKind.WebPage);
    public static string Documentation => Resolve(EnvironmentVariables.Persistent.DocumentationUrl, DefaultDocumentation, LinkKind.Markdown);
    public static string BugTracker => Resolve(EnvironmentVariables.Persistent.BugTrackerUrl, DefaultBugTracker, LinkKind.Markdown);

    /// <summary>
    /// <paramref name="stored"/> if it still validates, <paramref name="fallback"/> otherwise.
    /// Never returns null or empty, so every call site can use the result directly.
    /// </summary>
    public static string Resolve(string? stored, string fallback, LinkKind kind)
        => IsValid(stored, kind) ? stored!.Trim() : fallback;

    /// <summary>
    /// Whether a link is usable for its purpose. Absolute http(s) in both cases; a
    /// <see cref="LinkKind.Markdown"/> link additionally has to name a <c>.md</c> file, since
    /// the overlay fetches the raw bytes and runs Markdig over them - handed an HTML page it
    /// renders the markup as literal text rather than failing, which reads as a broken app
    /// rather than as a rejected setting.
    ///
    /// <para>The <c>.md</c> test looks at <see cref="Uri.AbsolutePath"/>, so a
    /// <c>#heading</c> fragment (which both defaults carry, and which the overlay honours by
    /// scrolling to it) doesn't disqualify the link.</para>
    /// </summary>
    public static bool IsValid(string? url, LinkKind kind)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        return kind != LinkKind.Markdown
            || uri.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The sentence a settings field shows when what was typed into it isn't accepted.</summary>
    public static string RejectionReason(LinkKind kind) => kind switch
    {
        LinkKind.Markdown => "Needs to be a full http:// or https:// address ending in .md",
        _ => "Needs to be a full http:// or https:// address"
    };
}
