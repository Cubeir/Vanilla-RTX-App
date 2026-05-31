using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace Vanilla_RTX_App.Core;

public sealed partial class PsaCard : UserControl
{
    private readonly string _text;
    private readonly PsaKind _kind;

    private const double FADE_IN_MS = 120;
    private const double FADE_OUT_MS = 120;

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
        ContentText.Text = item.Text;

        switch (item.Kind)
        {
            case PsaKind.Pinned:
                DismissButton.Visibility = Visibility.Collapsed;
                break;
            case PsaKind.Timed:
                ToolTipService.SetToolTip(DismissButton, "Dismiss for now");
                break;
            case PsaKind.Permanent:
                // tooltip already set to "Dismiss" in XAML
                break;
        }
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
                OnlineTexts.DismissTimed(_text);
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
