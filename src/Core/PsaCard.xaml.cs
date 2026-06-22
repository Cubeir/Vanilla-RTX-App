using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Diagnostics;

namespace Vanilla_RTX_App.Core;

public sealed partial class PsaCard : UserControl
{
    private readonly string _text;
    private readonly PsaKind _kind;
    private readonly int? _cooldownMinutes;

    private const double FADE_IN_MS = 50;
    private const double FADE_OUT_MS = 50;

    public double CardFontSize
    {
        get => ContentText.FontSize;
        set => ContentText.FontSize = value;
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
                ContentText.Opacity = 0.94;
                break;

            case PsaKind.Timed:
                ToolTipService.SetToolTip(DismissButton, FormatCooldownTooltip(_cooldownMinutes)); // tooltip
                CardBorder.Background = (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
                ContentText.Opacity = 0.89;
                break;

            case PsaKind.Permanent:
                // No background, no shadow, no extra padding — strip the Border visuals entirely
                CardBorder.Padding = new Thickness(0);
                CardBorder.Translation = System.Numerics.Vector3.Zero;
                CardBorder.Shadow = null;
                ContentText.Opacity = 0.8;
                break;
        }
    }
    private static string FormatCooldownTooltip(int? cooldownMinutes)
    {
        var minutes = cooldownMinutes ?? (int)OnlineTexts.TimedDuration.TotalMinutes;

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
            AnimateOpacity(DismissButton, to: 0.65, durationMs: FADE_IN_MS);
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_kind != PsaKind.Pinned)
            AnimateOpacity(DismissButton, to: 0, durationMs: FADE_OUT_MS);
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
        double fromHeight = this.ActualHeight;
        this.MaxHeight = fromHeight;

        var sb = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var collapse = new DoubleAnimation
        {
            From = fromHeight,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(collapse, this);
        Storyboard.SetTargetProperty(collapse, "MaxHeight");

        sb.Children.Add(fade);
        sb.Children.Add(collapse);
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
