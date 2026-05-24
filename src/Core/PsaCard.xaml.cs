using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace Vanilla_RTX_App.Core;

public sealed partial class PsaCard : UserControl
{
    private readonly string _text;
    private readonly bool _isEphemeral;

    private const double FADE_IN_MS = 120;
    private const double FADE_OUT_MS = 120;
    private const double DISMISS_MS = 180;

    public PsaCard(PsaItem item)
    {
        InitializeComponent();
        _text = item.Text;
        _isEphemeral = item.IsEphemeral;
        ContentText.Text = item.Text;
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
        => AnimateOpacity(DismissButton, to: 0.65, durationMs: FADE_IN_MS);

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
        => AnimateOpacity(DismissButton, to: 0, durationMs: FADE_OUT_MS);

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEphemeral)
            OnlineTexts.Dismiss(_text);

        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(DISMISS_MS)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
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
