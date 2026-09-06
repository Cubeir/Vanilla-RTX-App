using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Vanilla_RTX_App.Core;

public sealed partial class PsaCard : UserControl
{
    private readonly string _text;
    private readonly PsaKind _kind;
    private readonly int? _cooldownMinutes;

    private double AnimationSpeedMultiplier => EnvironmentVariables.Persistent.SuspendUIAnimations ? 0.01 : 1.0;
    private double FADE_IN_MS => 50 * AnimationSpeedMultiplier;
    private double FADE_OUT_MS => 50 * AnimationSpeedMultiplier;

    public double CardFontSize
    {
        get => ContentText.FontSize;
        set => ContentText.FontSize = value;
    }

    /// <summary>
    /// Shown in place of a module's announcements when we couldn't get any. Pinned, so it
    /// carries no dismiss button - there is nothing for the user to dismiss, it goes away on
    /// its own the next time the fetch works.
    /// </summary>
    private static readonly PsaItem RetrievalFailedNotice = new(
        "An error occurred when trying to retrieve the texts for this module, please try again " +
        "later and make sure the app has internet access. Some features may not work without internet.",
        PsaKind.Pinned,
        Glyph: "EB5E");

    /// <summary>
    /// Fills a module's announcement panel, falling back to a single notice card when there
    /// was nothing to fill it with.
    ///
    /// Several windows carve out a fixed space for these, and an empty one reads as a broken
    /// layout rather than as "no news" - which is what the user sees whenever the .md fetch
    /// fails outright.
    ///
    /// The fallback keys off the **source** array, not the filtered result, and that
    /// distinction is the whole rule:
    ///
    ///   - source null or empty  - the .md never arrived, or arrived without a section for
    ///                             this module. Both are our failure, so: notice card.
    ///   - source has items but GetFiltered returns null - the user dismissed them all.
    ///     That's their choice and an empty panel is the correct answer.
    ///
    /// Deliberately lives here rather than on OnlineTexts: this is a card, and the log-facing
    /// PSA feed reads OnlineTexts directly, so it can't pick this up by accident.
    /// </summary>
    public static void Populate(Panel host, PsaItem[]? source, double? cardFontSize = null)
    {
        if (host is null) return;

        host.Children.Clear();

        var items = source is null || source.Length == 0
            ? new[] { RetrievalFailedNotice }
            : OnlineTexts.GetFiltered(source);

        if (items is null) return;

        foreach (var item in items)
        {
            var card = new PsaCard(item);
            if (cardFontSize is { } size) card.CardFontSize = size;

            host.Children.Add(card);
        }
    }

    public PsaCard(PsaItem item)
    {
        InitializeComponent();
        _text = item.Text;
        _kind = item.Kind;
        _cooldownMinutes = item.CooldownMinutes;
        ContentText.Text = item.Text;

        // ── Glyph override ────────────────────────────────────────────────────
        // Value is a 4–5 char hex string e.g. "E946". Convert to the char the FontIcon expects.
        if (!string.IsNullOrEmpty(item.Glyph))
        {
            try
            {
                var codepoint = Convert.ToInt32(item.Glyph, 16);
                GlyphIcon.Glyph = char.ConvertFromUtf32(codepoint);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PsaCard] Failed to apply glyph '{item.Glyph}' — using default. {ex.Message}");
            }
        }

        // ── Per-kind background, opacity, dismiss setup ───────────────────────
        switch (item.Kind)
        {
            case PsaKind.Pinned:
                DismissButton.Visibility = Visibility.Collapsed;
                CardBorder.Background = (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
                ContentText.Opacity = 0.95;
                break;

            case PsaKind.Timed:
                ToolTipService.SetToolTip(DismissButton, FormatCooldownTooltip(_cooldownMinutes)); // tooltip
                CardBorder.Background = (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
                ContentText.Opacity = 0.9;
                break;

            case PsaKind.Permanent:
                CardBorder.Translation = System.Numerics.Vector3.Zero;
                CardBorder.Shadow = null;
                ContentText.Opacity = 0.85;
                break;
        }
    }
    private static string FormatCooldownTooltip(int? cooldownMinutes)
    {
        var minutes = cooldownMinutes ?? (int)OnlineTexts.TimedDuration.TotalMinutes;

        if (minutes == 0)
            return "Dismiss for now";

        if (minutes < 60)
            return $"Dismiss for {minutes} minute{(minutes == 1 ? "" : "s")}";

        var hours = (int)Math.Round(minutes / 60.0);
        if (hours < 24)
            return $"Dismiss for {hours} hour{(hours == 1 ? "" : "s")}";

        var days = (int)Math.Round(hours / 24.0);
        return $"Dismiss for {days} day{(days == 1 ? "" : "s")}";
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_kind != PsaKind.Pinned)
            AnimateOpacity(DismissButton, to: 1.0, durationMs: FADE_IN_MS);
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_kind != PsaKind.Pinned)
            AnimateOpacity(DismissButton, to: 0.0, durationMs: FADE_OUT_MS);
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_kind)
        {
            case PsaKind.Permanent:
                OnlineTexts.Dismiss(_text);
                break;
            case PsaKind.Timed:
                // Pass the per-item cooldown so [cd:""] overrides from the .md are respected.
                // If null, DismissTimed falls back to the global TIMED_DURATION.
                OnlineTexts.DismissTimed(_text, _cooldownMinutes);
                break;
            case PsaKind.Pinned:
                return; // button is hidden, should never fire
        }
        AnimateCollapse();
    }

    private void AnimateCollapse()
    {
        var sb = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160 * AnimationSpeedMultiplier)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");

        sb.Children.Add(fade);
        sb.Completed += (_, _) => Visibility = Visibility.Collapsed;
        sb.Begin();
    }

    private static void AnimateOpacity(UIElement target, double to, double durationMs)
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs))
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }
}
