using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Core;
using Windows.Storage;
using WinRT.Interop;
using WinUIEx;

namespace Vanilla_RTX_App.Modules.Alchitex;

public static class AlchitexVariables
{
    public static class Persistent
    {

    }
    public static class Defaults
    {

    }
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
                Trace.WriteLine($"[AlchitexVariables] An issue occured loading settings");
            }
        }
    }
}


public sealed partial class Alchitex : Window
{
    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;

    private static string LicenseAcceptedKey = $"Alchitex_LicenseAccepted_{TunerVariables.appVersion}";

    // ── Constructor ──────────────────────────────────────────────────────────
    public Alchitex(MainWindow mainWindow)
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

        // AppWindow / presenter
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            var dpi = this.GetDpiForWindow();
            var scaleFactor = dpi / 96.0;
            presenter.PreferredMinimumWidth = (int)(TunerVariables.WindowMinSizeX * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(TunerVariables.WindowMinSizeY * scaleFactor);
        }

        // Title bar
        if (_appWindow.TitleBar != null)
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(139, 139, 139, 139);
            _appWindow.TitleBar.InactiveForegroundColor = ColorHelper.FromArgb(128, 139, 139, 139);
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }

        this.SetIcon(System.IO.Path.Combine("Modules", "Alchitex", "Assets", "logo.large.ico"));

        this.Activated += Alchitex_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }
    // ── Lifetime ─────────────────────────────────────────────────────────────
    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _mainWindow.Closed -= MainWindow_Closed;
        this.Close();
    }

    private async void Alchitex_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);

        this.Activated -= Alchitex_Activated;

        SetTitleBar(TitleBarDragArea);

        PopulateAlchitexAnnouncements();

        await InitializeAsync();

        _ = this.DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(75);
            try { this.Activate(); } catch { }
        });
    }

    private void PopulateAlchitexAnnouncements()
    {
        var items = OnlineTexts.GetFiltered(OnlineTextsContent.AlchitexDevProgressUpdates);
        if (items is null) return;
        foreach (var item in items)
            AlchitexAnnouncementsPanel.Children.Add(new PsaCard(item));
    }

    // ── Init ─────────────────────────────────────────────────────────────────
    private async Task InitializeAsync()
    {
        try
        {
            bool accepted = await CheckLicenseAcceptedAsync();
            LoadingPanel.Visibility = Visibility.Collapsed;

            if (!accepted)
            {
                await PopulateLicenseTextAsync();
                LicensePanel.Visibility = Visibility.Visible;
            }
            else
            {
                ShowMainContent();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] EXCEPTION in Alchitex.InitializeAsync: {ex}");
            this.Close();
        }
    }
    // ── License ─────────────────────────────────────────
    private static Task<bool> CheckLicenseAcceptedAsync()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            var val = settings.Values[LicenseAcceptedKey];
            return Task.FromResult(val is bool b && b);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Error reading license key: {ex.Message}");
            return Task.FromResult(false);
        }
    }
    private async Task PopulateLicenseTextAsync()
    {
        try
        {
            var uri = new Uri("ms-appx:///Modules/Alchitex/LICENSE.txt");
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            var body = await FileIO.ReadTextAsync(file);

            LicenseTextBlock.Blocks.Clear();

            // ── "Online version" header ──────────────────────────────────────
            var headerPara = new Paragraph { Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 2) };
            headerPara.Inlines.Add(new Run { Text = "Online version:  " });
            var link = new Hyperlink
            {
                NavigateUri = new Uri("https://github.com/Cubeir/Vanilla-RTX-App/blob/main/src/Modules/Alchitex/LICENSE.txt")
            };
            link.Inlines.Add(new Run { Text = "View on GitHub" });
            headerPara.Inlines.Add(link);
            LicenseTextBlock.Blocks.Add(headerPara);

            // ── Separator ────────────────────────────────────────────────────
            var sepPara = new Paragraph { Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 8) };
            sepPara.Inlines.Add(new Run
            {
                Text = "───────────────────────────────────────",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            LicenseTextBlock.Blocks.Add(sepPara);

            // ── Body ─────────────────────────────────────────────────────────
            var bodyPara = new Paragraph();
            bodyPara.Inlines.Add(new Run { Text = body });
            LicenseTextBlock.Blocks.Add(bodyPara);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Error loading license text: {ex.Message}");
            LicenseTextBlock.Blocks.Clear();
            var err = new Paragraph();
            err.Inlines.Add(new Run { Text = $"Could not load license file: {ex.Message}" });
            LicenseTextBlock.Blocks.Add(err);
        }
    }
    private void DisagreeButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
    private async void AgreeButton_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => {
            try
            {
                ApplicationData.Current.LocalSettings.Values[LicenseAcceptedKey] = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Error writing license key: {ex.Message}");
            }
            return Task.CompletedTask;
        } ); 
        LicensePanel.Visibility = Visibility.Collapsed;
        ShowMainContent();
    }


    // ── Main Content ───────────────────────────────────────────────────────

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        _ = MainWindow.OpenUrl("http://minecraftrtx.net/reactor");
    }

    // ── Reveal main content ───────────────────────────────────────────────────

    // Main content are hidden before license is accepted, i.e. redstone circuits and others
    private async void ShowMainContent()
    {
        TitleBarText.Text = "RTX Reactor";
        InfoButton.Visibility = Visibility.Visible;
        MainGrid.Visibility = Visibility.Visible;
    }
}


/* ── The Backlogs and Scattered Ideas ─────────────────────────────────────────────────────
// Potentially rename to Alchemist, PBR Alchemist or RTX Reactor or ARCHITEX or ALCHETEX before release.

// Perfect the licensing windows' appearance

// Review: is it a good idea to limit features lifecycle to their windows? In general... should it all ahve been on the main window?
// well, you see, in your case, navigation view would've been very generic
// and some modules like alchitex might become too heavy, so yes, making the main window act like a nexus hub that spawns child apps is better...
// they have minimal communication/interactions, its like main window is a father responsible for them with all of the logs n things
// navigation view is also nice... think about it, just think, u love the way your buttons look, don't want them to go!

// REDSTONE ELEMENT IMPLEMENTAITON IDEA:
// We got the tile backgrounds
// Beneath there, have PROCEDURALLY GENERATED redstone going Upward from below, that makes 2 layers of bitmaps!
// still do it like u had in mind, tiles exist, images are dynamically selected based on neighbors
// Then, have a toggle, like the lamp, to either trigger random flashes, or continous random power flashes in the redstone
// A nice way to convey something being done in the background!
// This is the way, and is actually imeplementable, unlike earlier versions of the idea. (how were to understand which areas are... to trigger)
// it isn't too convoluted, and is gonna look AMAZING.
/// 
*/
