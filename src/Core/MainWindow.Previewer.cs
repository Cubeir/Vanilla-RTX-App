using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Vanilla_RTX_App.Core;

public class Previewer
{
    private readonly HashSet<string> _registeredPaths = new();
    private void RegisterPath(string? path)
    {
        if (!string.IsNullOrEmpty(path)) _registeredPaths.Add(path);
    }
    public async Task PreloadAllRegisteredImagesAsync()
    {
        await Task.WhenAll(_registeredPaths.Select(SharedImageCache.GetOrLoadAsync));
    }

    private static Previewer? _instance;
    private static readonly Lock _lock = new();

    private readonly Image _topVessel;
    private readonly Image _bottomVessel;
    private readonly Image _bg;
    private bool _mouseDown = false;
    private FrameworkElement? _activeControl = null;

    // Image caching to prevent flicker
    private string _currentBottomImage = "";
    private string _currentTopImage = "";

    // Cross-fade animation system
    private Storyboard? _currentTransition = null;
    private int _transitionId = 0;
    private bool _isTransitioning = false;
    private bool _forceTransitionForControlChange = false;

    // Which button (if any) currently owns a transient top-vessel click-flash. While set,
    // SetButtonPreview must not touch the top vessel for THIS button. If PointerEntered ever
    // re-fires for the same already-hovered button mid-flash, SetButtonPreview's topImagePath=""
    // would race the flash: either it lands early (top vessel still at opacity 0, nothing
    // "changed" yet) and collapses the top vessel outright, or it lands mid-flash and starts a
    // second, independent opacity animation on _topVessel via the shared crossfade system that
    // fights the flash's own (isolated) one for the same property. Either way the flash gets
    // cut off before it's seen.
    private Button? _flashingButton = null;

    // Click-flash's own storyboard/id tracking, deliberately separate from _currentTransition
    // /_transitionId above. Every hover/toggle/slider crossfade animates the bottom AND top
    // vessel in one shared Storyboard, and Stop()ing a Storyboard reverts EVERY property it
    // animates back to its pre-animation base value - so a flash that shared that bookkeeping
    // and called Stop() on it would silently snap the bottom vessel back to a stale opacity.
    private Storyboard? _flashTransition = null;
    private int _flashId = 0;

    // Configurable global transition settings
    public const double TransitionDurationPublic = 75;
    public double TransitionDurationMs { get; set; } = 75;
    public double OffFadeDelayThreshold { get; set; } = 1.0;

    // Freeze system - suspends all preview updates and snapshots current visual state
    public bool FreezeUpdates { get; set; } = false;
    private string _frozenBottomImage = "";
    private string _frozenTopImage = "";
    private double _frozenBottomOpacity = 0.0;
    private double _frozenTopOpacity = 0.0;

    // Singleton instance access
    public static Previewer Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        throw new InvalidOperationException("Previewer must be initialized first by calling Initialize()");
                    }
                }
            }
            return _instance;
        }
    }

    // Constructor
    private Previewer(Image topVessel, Image bottomVessel, Image backgroundVessel)
    {
        _topVessel = topVessel;
        _bottomVessel = bottomVessel;
        _bg = backgroundVessel;

        _bg.Opacity = 1.0;
        _bg.Visibility = Visibility.Visible;
        _topVessel.Opacity = 0.0;
        _topVessel.Visibility = Visibility.Collapsed;
        _bottomVessel.Opacity = 0.0;
        _bottomVessel.Visibility = Visibility.Collapsed;
    }

    // Initialize the singleton instance
    public static void Initialize(Image topVessel, Image bottomVessel, Image backgroundVessel)
    {
        if (_instance == null)
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new Previewer(topVessel, bottomVessel, backgroundVessel);
                }
            }
        }
    }

    // - - - - - Utility Methods


    public void SetImages(string imageOnPath, string imageOffPath, bool useSmoothTransition = false)
    {
        var bottomOpacity = !string.IsNullOrEmpty(imageOffPath) ? 1.0 : 0.0;
        var topOpacity = !string.IsNullOrEmpty(imageOnPath) ? 1.0 : 0.0;
        SetVesselState(imageOffPath ?? "", imageOnPath ?? "", bottomOpacity, topOpacity, useSmoothTransition);
    }

    /// <summary>
    /// Directly sets the bottom and/or top vessel images with a smooth transition.
    /// Call this after Unfreeze() at startup to display a chosen initial state.
    /// Pass null for either vessel to leave it unchanged.
    /// </summary>
    public void SetStartupImages(string? bottomImagePath = null, string? topImagePath = null)
    {
        string resolvedBottom = bottomImagePath ?? _currentBottomImage;
        string resolvedTop = topImagePath ?? _currentTopImage;

        double bottomOpacity = !string.IsNullOrEmpty(resolvedBottom) ? 1.0 : 0.0;
        double topOpacity = !string.IsNullOrEmpty(resolvedTop) ? 1.0 : 0.0;

        SetVesselState(resolvedBottom, resolvedTop, bottomOpacity, topOpacity, allowTransition: true);
    }

    public void ClearPreviews()
    {
        _currentBottomImage = "";
        _currentTopImage = "";
        _bottomVessel.Source = null;
        _topVessel.Source = null;
        _bottomVessel.Visibility = Visibility.Collapsed;
        _topVessel.Visibility = Visibility.Collapsed;
        //_bg.Visibility = Visibility.Collapsed;
        _bottomVessel.Opacity = 0.0;
        _topVessel.Opacity = 0.0;
        //_bg.Opacity = 0.0;
    }

    public void FadeAwayVessels(double targetOpacity, bool useSmoothTransition = true, int duration = (int)TransitionDurationPublic)
    {
        targetOpacity = Math.Clamp(targetOpacity, 0.0, 1.0);

        if (useSmoothTransition)
        {
            var fadeTop = new DoubleAnimation
            {
                From = _topVessel.Opacity,
                To = targetOpacity,
                Duration = TimeSpan.FromMilliseconds(duration)
            };

            var fadeBottom = new DoubleAnimation
            {
                From = _bottomVessel.Opacity,
                To = targetOpacity,
                Duration = TimeSpan.FromMilliseconds(duration)
            };

            var fadeBackground = new DoubleAnimation
            {
                From = _bg.Opacity,
                To = targetOpacity,
                Duration = TimeSpan.FromMilliseconds(duration)
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(fadeTop, _topVessel);
            Storyboard.SetTargetProperty(fadeTop, "Opacity");
            Storyboard.SetTarget(fadeBottom, _bottomVessel);
            Storyboard.SetTargetProperty(fadeBottom, "Opacity");
            Storyboard.SetTarget(fadeBackground, _bg);
            Storyboard.SetTargetProperty(fadeBackground, "Opacity");

            storyboard.Children.Add(fadeTop);
            storyboard.Children.Add(fadeBottom);
            storyboard.Children.Add(fadeBackground);

            _currentTransition?.Stop();
            _currentTransition = storyboard;
            _isTransitioning = true;

            storyboard.Completed += (s, e) =>
            {
                _isTransitioning = false;
                _currentTransition = null;

                if (Math.Abs(targetOpacity) < 0.01)
                {
                    _topVessel.Visibility = Visibility.Collapsed;
                    _bottomVessel.Visibility = Visibility.Collapsed;
                    _bg.Visibility = Visibility.Collapsed;
                }
            };

            storyboard.Begin();
        }
        else
        {
            _topVessel.Opacity = targetOpacity;
            _bottomVessel.Opacity = targetOpacity;
            _bg.Opacity = targetOpacity;

            if (Math.Abs(targetOpacity) < 0.01)
            {
                _topVessel.Visibility = Visibility.Collapsed;
                _bottomVessel.Visibility = Visibility.Collapsed;
                _bg.Visibility = Visibility.Collapsed;
            }
        }
    }

    // Freeze all preview updates and snapshot current visual state
    public void Freeze()
    {
        FreezeUpdates = true;

        // Snapshot currently visible state
        _frozenBottomImage = _currentBottomImage ?? "";
        _frozenTopImage = _currentTopImage ?? "";
        _frozenBottomOpacity = _bottomVessel.Opacity;
        _frozenTopOpacity = _topVessel.Opacity;
    }

    // Unfreeze and restore to the frozen snapshot
    public void Unfreeze()
    {
        FreezeUpdates = false;

        // Restore frozen state
        if (!string.IsNullOrEmpty(_frozenBottomImage) && _currentBottomImage != _frozenBottomImage)
        {
            _currentBottomImage = _frozenBottomImage;
            SetBottomVesselImage(_frozenBottomImage);
        }

        if (!string.IsNullOrEmpty(_frozenTopImage) && _currentTopImage != _frozenTopImage)
        {
            _currentTopImage = _frozenTopImage;
            SetTopVesselImage(_frozenTopImage);
        }

        _bottomVessel.Opacity = _frozenBottomOpacity;
        _topVessel.Opacity = _frozenTopOpacity;
    }

    // - - - - - Control Initialization Methods

    public void InitializeSlider(Slider slider, string defaultImagePath, string minImagePath, string maxImagePath, double defaultValue)
    {
        slider.SetValue(FrameworkElement.TagProperty, new SliderPreviewData
        {
            DefaultImagePath = defaultImagePath,
            MinImagePath = minImagePath,
            MaxImagePath = maxImagePath,
            DefaultValue = defaultValue
        });

        RegisterPath(defaultImagePath);
        RegisterPath(minImagePath);
        RegisterPath(maxImagePath);

        slider.ValueChanged += (s, e) => UpdateSliderPreview(slider);

        slider.PointerEntered += (s, e) =>
        {
            if (!_mouseDown || _activeControl == slider)
            {
                HandleControlChange(slider);
                UpdateSliderPreview(slider);
            }
        };

        slider.PointerPressed += (s, e) =>
        {
            _mouseDown = true;
            _activeControl = slider;
            slider.CapturePointer(e.Pointer);
            UpdateSliderPreview(slider);
        };

        slider.PointerReleased += (s, e) =>
        {
            _mouseDown = false;
            slider.ReleasePointerCapture(e.Pointer);
        };

        slider.PointerExited += (s, e) => { };

        slider.PointerCaptureLost += (s, e) =>
        {
            if (_mouseDown && _activeControl == slider)
            {
                _mouseDown = false;
                _activeControl = null;
            }
        };
    }

    /// <summary>
    /// Initializes a button with arrays of hover images and optional arrays of click images.
    /// On each hover from a different control, a new image is randomly selected from the array.
    /// Hovering the same button repeatedly keeps the same image.
    /// The bottom vessel always carries the hover image and is never touched by a click.
    /// If clickedImagePaths is provided, a click transiently fades a clicked image into the
    /// TOP vessel (overlaying the still-visible hover image below) and fades it back out - a
    /// deliberate blink, not a swap. When both arrays are the same length, the click image uses
    /// the same index as the current hover image, so identical arrays always flash the same
    /// image over itself (useful for art with transparent "lit" regions: stacking the same
    /// partially-transparent image over itself briefly intensifies the glow).
    /// </summary>
    public void InitializeButton(Button button, string[]? hoverImagePaths = null, string[]? clickedImagePaths = null)
    {
        button.SetValue(FrameworkElement.TagProperty, new ButtonPreviewData
        {
            ImageOffPaths = hoverImagePaths ?? [],
            ImageOnPaths = clickedImagePaths ?? [],
            CurrentRandomIndex = 0
        });

        foreach (var p in hoverImagePaths ?? []) RegisterPath(p);
        foreach (var p in clickedImagePaths ?? []) RegisterPath(p);

        button.PointerEntered += (s, e) =>
        {
            if (!_mouseDown || _activeControl == button)
            {
                HandleControlChange(button);         // re-rolls index only if coming from another control
                SetButtonPreview(button);
            }
        };

        button.PointerPressed += (s, e) =>
        {
            _mouseDown = true;
            _activeControl = button;
            button.CapturePointer(e.Pointer);
        };

        button.PointerReleased += (s, e) =>
        {
            _mouseDown = false;
            button.ReleasePointerCapture(e.Pointer);
        };

        // ButtonBase marks PointerPressed/PointerReleased Handled as part of its own click
        // machinery, so a plain instance PointerPressed subscription (above) never actually
        // sees a mouse click - only PointerEntered/PointerExited (not part of that machinery)
        // fire reliably. Click is ButtonBase's own synthesized "activated" event and isn't
        // subject to that, so the flash has to trigger from here instead.
        button.Click += (s, e) =>
        {
            var data = button.GetValue(FrameworkElement.TagProperty) as ButtonPreviewData;
            if (data != null && data.ImageOnPaths.Length > 0)
            {
                FlashButtonClickOverlay(button, data);
            }
        };

        button.PointerExited += (s, e) => { };

        button.PointerCaptureLost += (s, e) =>
        {
            if (_mouseDown && _activeControl == button)
            {
                _mouseDown = false;
                _activeControl = null;
            }
        };
    }

    /// <summary>
    /// Convenience overload: single hover image, optional single clicked image.
    /// Wraps strings into single-element arrays so everything routes through the same path.
    /// </summary>
    public void InitializeButton(Button button, string? hoverImagePath, string? clickedImagePath = null)
    {
        InitializeButton(
            button,
            hoverImagePath != null ? [hoverImagePath] : null,
            clickedImagePath != null ? [clickedImagePath] : null
        );
    }

    public void InitializeToggleButton(ToggleButton toggleButton, string imageOnPath, string imageOffPath)
    {
        toggleButton.SetValue(FrameworkElement.TagProperty, new TogglePreviewData
        {
            ImageOnPath = imageOnPath,
            ImageOffPath = imageOffPath
        });

        RegisterPath(imageOnPath);
        RegisterPath(imageOffPath);

        toggleButton.Checked += (s, e) =>
        {
            if (_activeControl == toggleButton)
            {
                SetTogglePreview(toggleButton, true);
            }
        };

        toggleButton.Unchecked += (s, e) =>
        {
            if (_activeControl == toggleButton)
            {
                SetTogglePreview(toggleButton, false);
            }
        };

        toggleButton.PointerEntered += (s, e) =>
        {
            if (!_mouseDown || _activeControl == toggleButton)
            {
                HandleControlChange(toggleButton);
                SetTogglePreview(toggleButton, toggleButton.IsChecked ?? false);
            }
        };

        toggleButton.PointerPressed += (s, e) =>
        {
            _mouseDown = true;
            _activeControl = toggleButton;
            toggleButton.CapturePointer(e.Pointer);
            bool currentState = toggleButton.IsChecked ?? false;
            SetTogglePreview(toggleButton, !currentState);
        };

        toggleButton.PointerReleased += (s, e) =>
        {
            _mouseDown = false;
            toggleButton.ReleasePointerCapture(e.Pointer);
        };

        toggleButton.PointerExited += (s, e) => { };

        toggleButton.PointerCaptureLost += (s, e) =>
        {
            if (_mouseDown && _activeControl == toggleButton)
            {
                _mouseDown = false;
                _activeControl = null;
            }
        };
    }

    public void InitializeToggleSwitch(ToggleSwitch toggleSwitch, string imageOnPath, string imageOffPath)
    {
        toggleSwitch.SetValue(FrameworkElement.TagProperty, new TogglePreviewData
        {
            ImageOnPath = imageOnPath,
            ImageOffPath = imageOffPath
        });

        RegisterPath(imageOnPath);
        RegisterPath(imageOffPath);

        toggleSwitch.Toggled += (s, e) =>
        {
            if (_activeControl == toggleSwitch)
            {
                SetTogglePreview(toggleSwitch, toggleSwitch.IsOn);
            }
        };

        toggleSwitch.PointerEntered += (s, e) =>
        {
            if (!_mouseDown || _activeControl == toggleSwitch)
            {
                HandleControlChange(toggleSwitch);
                SetTogglePreview(toggleSwitch, toggleSwitch.IsOn);
            }
        };

        toggleSwitch.PointerPressed += (s, e) =>
        {
            _mouseDown = true;
            _activeControl = toggleSwitch;
            toggleSwitch.CapturePointer(e.Pointer);
            SetTogglePreview(toggleSwitch, !toggleSwitch.IsOn);
        };

        toggleSwitch.PointerReleased += (s, e) =>
        {
            _mouseDown = false;
            toggleSwitch.ReleasePointerCapture(e.Pointer);
        };

        toggleSwitch.PointerExited += (s, e) => { };

        toggleSwitch.PointerCaptureLost += (s, e) =>
        {
            if (_mouseDown && _activeControl == toggleSwitch)
            {
                _mouseDown = false;
                _activeControl = null;
            }
        };
    }

    public void InitializeCheckBox(CheckBox checkBox, string imageOnPath, string imageOffPath)
    {
        checkBox.SetValue(FrameworkElement.TagProperty, new TogglePreviewData
        {
            ImageOnPath = imageOnPath,
            ImageOffPath = imageOffPath
        });

        RegisterPath(imageOnPath);
        RegisterPath(imageOffPath);

        checkBox.Checked += (s, e) =>
        {
            if (_activeControl == checkBox)
            {
                SetTogglePreview(checkBox, true);
            }
        };

        checkBox.Unchecked += (s, e) =>
        {
            if (_activeControl == checkBox)
            {
                SetTogglePreview(checkBox, false);
            }
        };

        checkBox.PointerEntered += (s, e) =>
        {
            if (!_mouseDown || _activeControl == checkBox)
            {
                HandleControlChange(checkBox);
                SetTogglePreview(checkBox, checkBox.IsChecked ?? false);
            }
        };

        checkBox.PointerPressed += (s, e) =>
        {
            _mouseDown = true;
            _activeControl = checkBox;
            checkBox.CapturePointer(e.Pointer);
            bool currentState = checkBox.IsChecked ?? false;
            SetTogglePreview(checkBox, !currentState);
        };

        checkBox.PointerReleased += (s, e) =>
        {
            _mouseDown = false;
            checkBox.ReleasePointerCapture(e.Pointer);
        };

        checkBox.PointerExited += (s, e) => { };

        checkBox.PointerCaptureLost += (s, e) =>
        {
            if (_mouseDown && _activeControl == checkBox)
            {
                _mouseDown = false;
                _activeControl = null;
            }
        };
    }

    // - - - - - Private Helper Methods

    private void HandleControlChange(FrameworkElement newControl)
    {
        bool isControlChange = _activeControl != newControl && _activeControl != null;
        _activeControl = newControl;

        if (isControlChange)
        {
            _forceTransitionForControlChange = true;

            // Focus has genuinely moved elsewhere - any flash the old control owned no longer
            // has a claim on the top vessel.
            _flashingButton = null;

            // Re-roll random index only when genuinely arriving from a different control
            if (newControl.GetValue(FrameworkElement.TagProperty) is ButtonPreviewData data && data.ImageOffPaths.Length > 1)
            {
                data.CurrentRandomIndex = Random.Shared.Next(0, data.ImageOffPaths.Length);
            }
        }

        if (FreezeUpdates)
        {
            return;
        }

        FadeInBackground();
    }

    private void FadeInBackground(double durationMs = TransitionDurationPublic)
    {
        if (FreezeUpdates)
            return;

        if (_bg.Visibility == Visibility.Visible && _bg.Opacity >= 1.0)
            return;

        _bg.Opacity = 0;
        _bg.Visibility = Visibility.Visible;

        _bg.DispatcherQueue.TryEnqueue(() =>
        {
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var sb = new Storyboard();
            Storyboard.SetTarget(fadeIn, _bg);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            sb.Children.Add(fadeIn);
            sb.Begin();
        });
    }

    private void UpdateSliderPreview(Slider slider)
    {
        if (FreezeUpdates) return;

        var data = slider.GetValue(FrameworkElement.TagProperty) as SliderPreviewData;
        if (data == null) return;

        double currentValue = slider.Value;
        double minValue = slider.Minimum;
        double maxValue = slider.Maximum;
        double defaultValue = data.DefaultValue;

        string targetBottomImage = data.DefaultImagePath ?? "";
        double targetBottomOpacity = !string.IsNullOrEmpty(data.DefaultImagePath) ? 1.0 : 0.0;

        bool bothMinMaxNull = string.IsNullOrEmpty(data.MinImagePath) && string.IsNullOrEmpty(data.MaxImagePath);
        string targetTopImage = "";
        double targetTopOpacity = 0.0;

        if (!bothMinMaxNull)
        {
            if (currentValue >= defaultValue)
            {
                targetTopImage = data.MaxImagePath ?? "";
                if (!string.IsNullOrEmpty(targetTopImage))
                {
                    if (maxValue == defaultValue)
                    {
                        targetTopOpacity = 0.0;
                    }
                    else
                    {
                        double progress = (currentValue - defaultValue) / (maxValue - defaultValue);
                        targetTopOpacity = Math.Min(1.0, Math.Max(0.0, progress));
                    }
                }
            }
            else
            {
                targetTopImage = data.MinImagePath ?? "";
                if (!string.IsNullOrEmpty(targetTopImage))
                {
                    if (minValue == defaultValue)
                    {
                        targetTopOpacity = 0.0;
                    }
                    else
                    {
                        double progress = (defaultValue - currentValue) / (defaultValue - minValue);
                        targetTopOpacity = Math.Min(1.0, Math.Max(0.0, progress));
                    }
                }
            }
        }

        bool useTransition = _forceTransitionForControlChange;
        _forceTransitionForControlChange = false;

        SetVesselState(targetBottomImage, targetTopImage, targetBottomOpacity, targetTopOpacity, useTransition);
    }

    private void SetButtonPreview(Button button)
    {
        if (FreezeUpdates) return;

        // A click-flash on this exact button currently owns the top vessel - see _flashingButton.
        if (_flashingButton == button) return;

        var data = button.GetValue(FrameworkElement.TagProperty) as ButtonPreviewData;
        if (data == null) return;

        var pool = data.ImageOffPaths;
        if (pool.Length == 0) return;

        string imagePath = pool[Math.Clamp(data.CurrentRandomIndex, 0, pool.Length - 1)];

        double bottomOpacity = !string.IsNullOrEmpty(imagePath) ? 1.0 : 0.0;
        SetVesselState(imagePath, "", bottomOpacity, 0.0, true);
    }

    /// <summary>
    /// Transiently fades a clicked-pool image into the top vessel over the still-visible
    /// hover image on the bottom vessel, holds briefly, then fades it back out. Bypasses
    /// SetVesselState entirely - that method always cross-fades both vessels together in ONE
    /// storyboard, and this flash must never touch a storyboard that also carries the bottom
    /// vessel: calling Stop() on a Storyboard reverts EVERY property it animates back to its
    /// pre-animation base value, not just the one being re-targeted. Sharing _currentTransition
    /// with the hover/toggle/slider crossfade system was tried and caused exactly that - the
    /// flash's own Stop() call was silently snapping the bottom (hover) vessel back to its
    /// mid-transition base opacity (often 0), which is why it looked like the whole preview was
    /// blinking rather than just the top layer. _flashTransition/_flashId below are this flash's
    /// own, entirely separate from the shared crossfade bookkeeping.
    /// </summary>
    private void FlashButtonClickOverlay(Button button, ButtonPreviewData data)
    {
        if (FreezeUpdates) return;
        if (data.ImageOnPaths.Length == 0) return;

        // Same index as the hover image when the pools are the same size, so identical
        // arrays always flash the very image already showing on the bottom vessel.
        int index = data.ImageOnPaths.Length == data.ImageOffPaths.Length
            ? data.CurrentRandomIndex
            : Random.Shared.Next(0, data.ImageOnPaths.Length);
        index = Math.Clamp(index, 0, data.ImageOnPaths.Length - 1);
        string flashImagePath = data.ImageOnPaths[index];

        _flashingButton = button;

        SetTopVesselImage(flashImagePath);
        _currentTopImage = flashImagePath;
        _topVessel.Visibility = Visibility.Visible;

        _flashTransition?.Stop();
        int flashId = ++_flashId;

        var fadeIn = new DoubleAnimation
        {
            From = _topVessel.Opacity,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(TransitionDurationMs)
        };

        var sbIn = new Storyboard();
        Storyboard.SetTarget(fadeIn, _topVessel);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        sbIn.Children.Add(fadeIn);

        sbIn.Completed += (s, e) =>
        {
            // Bail if superseded by a newer flash, or if focus has genuinely moved to another
            // control - in that case the new control's own crossfade already owns the top
            // vessel and this stale flash has nothing correct left to do.
            if (flashId != _flashId || _activeControl != button) return;
            _ = FadeOutButtonClickOverlayAsync(button, flashId);
        };

        _flashTransition = sbIn;
        sbIn.Begin();
    }

    private async Task FadeOutButtonClickOverlayAsync(Button button, int flashId)
    {
        await Task.Delay(50);
        if (flashId != _flashId || FreezeUpdates || _activeControl != button) return;

        var fadeOut = new DoubleAnimation
        {
            From = _topVessel.Opacity,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(TransitionDurationMs)
        };

        var sbOut = new Storyboard();
        Storyboard.SetTarget(fadeOut, _topVessel);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        sbOut.Children.Add(fadeOut);

        sbOut.Completed += (s, e) =>
        {
            if (_flashingButton == button) _flashingButton = null;
        };

        _flashTransition?.Stop();
        _flashTransition = sbOut;
        sbOut.Begin();
    }

    private void SetTogglePreview(FrameworkElement element, bool isOn)
    {
        if (FreezeUpdates) return;

        var data = element.GetValue(FrameworkElement.TagProperty) as TogglePreviewData;
        if (data == null) return;

        string bottomImage = data.ImageOffPath ?? "";
        string topImage = data.ImageOnPath ?? "";

        double bottomOpacity;
        double topOpacity;

        if (isOn && !string.IsNullOrEmpty(topImage))
        {
            topOpacity = 1.0;
            bottomOpacity = 0.0;
        }
        else if (!isOn && !string.IsNullOrEmpty(bottomImage))
        {
            topOpacity = 0.0;
            bottomOpacity = 1.0;
        }
        else
        {
            topOpacity = !string.IsNullOrEmpty(topImage) && isOn ? 1.0 : 0.0;
            bottomOpacity = !string.IsNullOrEmpty(bottomImage) && !isOn ? 1.0 : 0.0;
        }

        SetVesselState(bottomImage, topImage, bottomOpacity, topOpacity, true);
    }

    private void SetVesselState(string bottomImagePath, string topImagePath, double bottomOpacity, double topOpacity, bool allowTransition)
    {
        if (FreezeUpdates) return;

        bool bottomImageChanged = !string.IsNullOrEmpty(bottomImagePath) && _currentBottomImage != bottomImagePath;
        bool topImageChanged = !string.IsNullOrEmpty(topImagePath) && _currentTopImage != topImagePath;
        bool bottomOpacityChanged = Math.Abs(_bottomVessel.Opacity - bottomOpacity) > 0.01;
        bool topOpacityChanged = Math.Abs(_topVessel.Opacity - topOpacity) > 0.01;

        bool forcedByControlChange = _forceTransitionForControlChange;
        _forceTransitionForControlChange = false;

        bool needsTransition = allowTransition && (forcedByControlChange ||
            bottomImageChanged || topImageChanged ||
            bottomOpacityChanged && (_bottomVessel.Opacity == 0.0 || bottomOpacity == 0.0) ||
            topOpacityChanged && (_topVessel.Opacity == 0.0 || topOpacity == 0.0));

        if (!needsTransition)
        {
            ApplyVesselState(bottomImagePath, topImagePath, bottomOpacity, topOpacity, false);
            return;
        }

        // Start cross-fade transition
        if (_isTransitioning)
        {
            _currentTransition?.Stop();
        }

        _isTransitioning = true;
        int currentTransitionId = ++_transitionId;

        if (bottomImageChanged || topImageChanged)
        {
            // Fade out, swap images, fade in
            var fadeOutTop = new DoubleAnimation
            {
                From = _topVessel.Opacity,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(TransitionDurationMs)
            };

            var fadeOutBottom = new DoubleAnimation
            {
                From = _bottomVessel.Opacity,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(TransitionDurationMs)
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(fadeOutTop, _topVessel);
            Storyboard.SetTargetProperty(fadeOutTop, "Opacity");
            Storyboard.SetTarget(fadeOutBottom, _bottomVessel);
            Storyboard.SetTargetProperty(fadeOutBottom, "Opacity");

            storyboard.Children.Add(fadeOutTop);
            storyboard.Children.Add(fadeOutBottom);

            storyboard.Completed += (s, e) =>
            {
                if (currentTransitionId != _transitionId)
                {
                    _isTransitioning = false;
                    return;
                }

                // Apply the new images while faded out (but keep vessels visible for fade-in)
                ApplyVesselState(bottomImagePath, topImagePath, 0.0, 0.0, true);

                // Fade in to target opacities
                FadeInToTargetOpacities(bottomOpacity, topOpacity, currentTransitionId);
            };

            _currentTransition = storyboard;
            storyboard.Begin();
        }
        else
        {
            // Just animate opacity change
            FadeToTargetOpacities(bottomOpacity, topOpacity, currentTransitionId);
        }
    }

    private void FadeInToTargetOpacities(double targetBottomOpacity, double targetTopOpacity, int transitionId)
    {
        // No Visibility guard needed here (unlike FadeToTargetOpacities below): this method's
        // one call site always runs right after ApplyVesselState(..., duringTransition: true),
        // which already forces both vessels Visible unconditionally.
        var fadeInTop = new DoubleAnimation
        {
            From = 0.0,
            To = targetTopOpacity,
            Duration = TimeSpan.FromMilliseconds(TransitionDurationMs)
        };

        var fadeInBottom = new DoubleAnimation
        {
            From = 0.0,
            To = targetBottomOpacity,
            Duration = TimeSpan.FromMilliseconds(TransitionDurationMs)
        };

        bool topAppearingAndBottomDisappearing = targetTopOpacity > 0.0 && targetBottomOpacity == 0.0;
        if (topAppearingAndBottomDisappearing)
        {
            fadeInBottom.BeginTime = TimeSpan.FromMilliseconds(TransitionDurationMs * OffFadeDelayThreshold);
        }

        bool bottomAppearingAndTopDisappearing = targetBottomOpacity > 0.0 && targetTopOpacity == 0.0;
        if (bottomAppearingAndTopDisappearing)
        {
            fadeInTop.BeginTime = TimeSpan.FromMilliseconds(TransitionDurationMs * OffFadeDelayThreshold);
        }

        var storyboard = new Storyboard();
        Storyboard.SetTarget(fadeInTop, _topVessel);
        Storyboard.SetTargetProperty(fadeInTop, "Opacity");
        Storyboard.SetTarget(fadeInBottom, _bottomVessel);
        Storyboard.SetTargetProperty(fadeInBottom, "Opacity");

        storyboard.Children.Add(fadeInTop);
        storyboard.Children.Add(fadeInBottom);

        storyboard.Completed += (s, e) =>
        {
            if (transitionId == _transitionId)
            {
                _isTransitioning = false;
            }
        };

        _currentTransition = storyboard;
        storyboard.Begin();
    }

    private void FadeToTargetOpacities(double targetBottomOpacity, double targetTopOpacity, int transitionId)
    {
        // Unlike FadeInToTargetOpacities, this is reached directly from SetVesselState's
        // opacity-only branch with no preceding ApplyVesselState call - so it's the only place
        // that can restore a vessel's Visibility before animating it toward visible opacity.
        if (targetBottomOpacity > 0.0) _bottomVessel.Visibility = Visibility.Visible;
        if (targetTopOpacity > 0.0) _topVessel.Visibility = Visibility.Visible;

        var fadeTop = new DoubleAnimation
        {
            From = _topVessel.Opacity,
            To = targetTopOpacity,
            Duration = TimeSpan.FromMilliseconds(TransitionDurationMs)
        };

        var fadeBottom = new DoubleAnimation
        {
            From = _bottomVessel.Opacity,
            To = targetBottomOpacity,
            Duration = TimeSpan.FromMilliseconds(TransitionDurationMs)
        };

        bool topIncreasing = targetTopOpacity > _topVessel.Opacity;
        bool bottomGoingToZero = Math.Abs(targetBottomOpacity - 0.0) < 0.001 && _bottomVessel.Opacity > 0.0;

        if (topIncreasing && bottomGoingToZero)
        {
            fadeBottom.BeginTime = TimeSpan.FromMilliseconds(TransitionDurationMs * OffFadeDelayThreshold);
        }

        bool bottomIncreasing = targetBottomOpacity > _bottomVessel.Opacity;
        bool topGoingToZero = Math.Abs(targetTopOpacity - 0.0) < 0.001 && _topVessel.Opacity > 0.0;

        if (bottomIncreasing && topGoingToZero)
        {
            fadeTop.BeginTime = TimeSpan.FromMilliseconds(TransitionDurationMs * OffFadeDelayThreshold);
        }

        var storyboard = new Storyboard();
        Storyboard.SetTarget(fadeTop, _topVessel);
        Storyboard.SetTargetProperty(fadeTop, "Opacity");
        Storyboard.SetTarget(fadeBottom, _bottomVessel);
        Storyboard.SetTargetProperty(fadeBottom, "Opacity");

        storyboard.Children.Add(fadeTop);
        storyboard.Children.Add(fadeBottom);

        storyboard.Completed += (s, e) =>
        {
            if (transitionId == _transitionId)
            {
                _isTransitioning = false;
            }
        };

        _currentTransition = storyboard;
        storyboard.Begin();
    }

    private void ApplyVesselState(string bottomImagePath, string topImagePath, double bottomOpacity, double topOpacity, bool duringTransition)
    {
        // Apply bottom vessel image
        if (!string.IsNullOrEmpty(bottomImagePath) && _currentBottomImage != bottomImagePath)
        {
            SetBottomVesselImage(bottomImagePath);
            _currentBottomImage = bottomImagePath;
        }
        else if (string.IsNullOrEmpty(bottomImagePath))
        {
            // Clearing the tracked path too, not just the visuals - otherwise a later
            // call for this same path looks "unchanged" and skips both the image
            // assignment and the visibility restore below (the bug this guards against).
            _currentBottomImage = "";
        }

        // Visibility tracks the vessel's target opacity, not whether the path changed -
        // an opacity-only fade (no path change) still needs the vessel to be visible.
        if (bottomOpacity > 0.0 || duringTransition)
        {
            _bottomVessel.Visibility = Visibility.Visible;
        }
        else
        {
            _bottomVessel.Visibility = Visibility.Collapsed;
        }

        // Apply top vessel image
        if (!string.IsNullOrEmpty(topImagePath) && _currentTopImage != topImagePath)
        {
            SetTopVesselImage(topImagePath);
            _currentTopImage = topImagePath;
        }
        else if (string.IsNullOrEmpty(topImagePath))
        {
            _currentTopImage = "";
        }

        if (topOpacity > 0.0 || duringTransition)
        {
            _topVessel.Visibility = Visibility.Visible;
        }
        else
        {
            _topVessel.Visibility = Visibility.Collapsed;
        }

        // Set opacities
        _bottomVessel.Opacity = bottomOpacity;
        _topVessel.Opacity = topOpacity;
    }

    private void SetBottomVesselImage(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return;

        if (SharedImageCache.TryGet(imagePath, out var cached) && cached != null)
        {
            _bottomVessel.Source = cached;
        }
        else
        {
            _bottomVessel.Source = new BitmapImage(new Uri(imagePath));
            _ = SharedImageCache.GetOrLoadAsync(imagePath);
        }
    }

    private void SetTopVesselImage(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return;

        if (SharedImageCache.TryGet(imagePath, out var cached) && cached != null)
        {
            _topVessel.Source = cached;
        }
        else
        {
            _topVessel.Source = new BitmapImage(new Uri(imagePath));
            _ = SharedImageCache.GetOrLoadAsync(imagePath);
        }
    }

    // - - - - - Data Classes

    private class SliderPreviewData
    {
        public string? DefaultImagePath { get; set; }
        public string? MinImagePath { get; set; }
        public string? MaxImagePath { get; set; }
        public double DefaultValue { get; set; }
    }

    private class ButtonPreviewData
    {
        public string[] ImageOffPaths { get; set; } = [];
        public string[] ImageOnPaths { get; set; } = [];
        public int CurrentRandomIndex { get; set; } = 0;
    }

    private class TogglePreviewData
    {
        public string? ImageOnPath { get; set; }
        public string? ImageOffPath { get; set; }
    }
}
