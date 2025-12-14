using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Vanilla_RTX_App.Core;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.PackUpdate;

public sealed partial class PackUpdateWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;

    public PackUpdateWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();
        _mainWindow = mainWindow;

        // Theme
        var mode = TunerVariables.Persistent.AppThemeMode ?? "System";
        if (this.Content is FrameworkElement root)
        {
            root.RequestedTheme = mode switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            var dpi = MainWindow.GetDpiForWindow(hWnd);
            var scaleFactor = dpi / 96.0;
            presenter.PreferredMinimumWidth = (int)(900 * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(600 * scaleFactor);
        }

        if (_appWindow.TitleBar != null)
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(139, 139, 139, 139);
            _appWindow.TitleBar.InactiveForegroundColor = ColorHelper.FromArgb(128, 139, 139, 139);
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }

        this.Activated += PackUpdateWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        // Unsubscribe to avoid memory leaks
        _mainWindow.Closed -= MainWindow_Closed;
        this.Close();
    }

    private async void PackUpdateWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            // Unsub
            this.Activated -= PackUpdateWindow_Activated;

            // Delay drag region setup until UI is fully loaded
            _ = this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                SetTitleBarDragRegion();
            });

            // Setup shadows for panels
            SetupShadows();

            // TODO: Load version info and setup button handlers
            // This will be implemented in the next phase
        }
    }

    private void SetTitleBarDragRegion()
    {
        if (_appWindow.TitleBar != null && TitleBarArea.XamlRoot != null)
        {
            try
            {
                var scaleAdjustment = TitleBarArea.XamlRoot.RasterizationScale;
                var dragRectHeight = (int)(TitleBarArea.ActualHeight * scaleAdjustment);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting drag region: {ex.Message}");
            }
        }
    }

    private void SetupShadows()
    {
        // Setup shadow receivers for all panels
        // Shadows are already defined in XAML, just need to ensure receivers are set
        if (ShadowReceiverGrid != null)
        {
            // Shadow setup is handled in XAML with Translation and ThemeShadow
            // No additional code needed - kept here for future enhancement
        }
    }
}
