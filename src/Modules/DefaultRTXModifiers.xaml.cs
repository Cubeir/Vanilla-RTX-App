using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.RTXDefaults;

// TODO: add a note, Not compatible with BetterRTX
// Also an idea, expand on it GREATLY, color pickers and sliders...!! let user adjust the Luts easily?
// Or you could keep it simple and user friendly as it is, and give your own favorite LUT to everyone
// Or better yet. come up with a Preset system, have several lut presets made, matrix, grey, ultra bright, etc... let users swap between them
// This is actually a better idea than anything, include the default lut, have a few default lut presets

// Like dlss swapper, mends the game automatically IF our defaults cache is corrupt AND the game files are corrupt too, it will treat the app's lut as the default and mend the game with it
// Maybe the logic isn't entirely sound, think about it...

// Yup, definitely add a preset system.
// The images can change, the texts remain the same across presets
// It'll be relatively simple to do, claude can do.

public sealed partial class DefaultRTXModifiersWindow : Window
{
    private const string FnLut = "look_up_tables.png";
    private const string FnSky = "sky.png";
    private const string FnWater = "water_n.tga";

    private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;

    private static string SrcLut => Path.Combine(AppDir, "Assets", "ray_tracing", FnLut);
    private static string SrcSky => Path.Combine(AppDir, "Assets", "ray_tracing", FnSky);
    private static string SrcWater => Path.Combine(AppDir, "Assets", "ray_tracing", FnWater);

    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;

    private string _minecraftRoot;
    private string _backupFolder;
    private CancellationTokenSource _scanCancellationTokenSource;

    public bool OperationSuccessful { get; private set; } = false;
    public string StatusMessage { get; private set; } = "";

    // Destination paths — built fresh from _minecraftRoot each access
    private string DstLut => Path.Combine(_minecraftRoot, "Content", "data", "ray_tracing", FnLut);
    private string DstSky => Path.Combine(_minecraftRoot, "Content", "data", "ray_tracing", FnSky);
    private string DstWater => Path.Combine(_minecraftRoot, "Content", "data", "ray_tracing", FnWater);

    private string BackupLut => Path.Combine(_backupFolder, FnLut);
    private string BackupSky => Path.Combine(_backupFolder, FnSky);
    private string BackupWater => Path.Combine(_backupFolder, FnWater);

    public DefaultRTXModifiersWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();
        _mainWindow = mainWindow;

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
            presenter.PreferredMinimumWidth = (int)(WindowMinSizeX * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(WindowMinSizeY * scaleFactor);
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

        this.Activated += DefaultRTXModifiersWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();
        _mainWindow.Closed -= MainWindow_Closed;
        this.Close();
    }

    private async void DefaultRTXModifiersWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            this.Activated -= DefaultRTXModifiersWindow_Activated;

            _ = this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => SetTitleBarDragRegion());

            var target = TunerVariables.Persistent.IsTargetingPreview
                ? "Minecraft Preview"
                : "Minecraft Release";
            WindowTitle.Text = $"Default RTX Modifiers - {target}";

            ManualSelectionText.Text =
                "If this is taking too long, click to manually locate the game folder. " +
                "Confirm in File Explorer once you're inside the folder called: " +
                (TunerVariables.Persistent.IsTargetingPreview
                    ? MinecraftGDKLocator.MinecraftPreviewFolderName
                    : MinecraftGDKLocator.MinecraftFolderName);

            await InitializeAsync();

            _ = this.DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(75);
                try { this.Activate(); } catch { }
            });
        }
    }

    private void SetTitleBarDragRegion()
    {
        if (_appWindow.TitleBar != null && TitleBarArea.XamlRoot != null)
        {
            try
            {
                var _ = (int)(TitleBarArea.ActualHeight * TitleBarArea.XamlRoot.RasterizationScale);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error setting drag region: {ex.Message}");
            }
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var isPreview = TunerVariables.Persistent.IsTargetingPreview;
            var cachedPath = isPreview
                ? TunerVariables.Persistent.MinecraftPreviewInstallPath
                : TunerVariables.Persistent.MinecraftInstallPath;

            string minecraftPath = null;

            if (MinecraftGDKLocator.RevalidateCachedPath(cachedPath))
            {
                Trace.WriteLine($"RTXW: Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Trace.WriteLine("RTXW: Cache became invalid, clearing");
                    if (isPreview)
                        TunerVariables.Persistent.MinecraftPreviewInstallPath = null;
                    else
                        TunerVariables.Persistent.MinecraftInstallPath = null;
                }

                _ = this.DispatcherQueue.TryEnqueue(() =>
                    ManualSelectionButton.Visibility = Visibility.Visible);

                _scanCancellationTokenSource = new CancellationTokenSource();
                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    isPreview, _scanCancellationTokenSource.Token);

                if (minecraftPath == null)
                {
                    Trace.WriteLine("RTXW: System search cancelled or failed");
                    return;
                }
            }

            await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RTXW EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }

    private async Task ContinueInitializationWithPath(string minecraftPath)
    {
        _minecraftRoot = minecraftPath;

        Trace.WriteLine($"RTXW: Game root  : {_minecraftRoot}");
        Trace.WriteLine($"RTXW: AppDir     : {AppDir}");
        Trace.WriteLine($"RTXW: SrcLut     : {SrcLut}  exists={File.Exists(SrcLut)}");
        Trace.WriteLine($"RTXW: SrcSky     : {SrcSky}  exists={File.Exists(SrcSky)}");
        Trace.WriteLine($"RTXW: SrcWater   : {SrcWater}  exists={File.Exists(SrcWater)}");
        Trace.WriteLine($"RTXW: DstLut     : {DstLut}  exists={File.Exists(DstLut)}");
        Trace.WriteLine($"RTXW: DstSky     : {DstSky}  exists={File.Exists(DstSky)}");
        Trace.WriteLine($"RTXW: DstWater   : {DstWater}  exists={File.Exists(DstWater)}");

        _backupFolder = EstablishBackupFolder();
        if (_backupFolder == null)
        {
            StatusMessage = "Could not establish backup folder";
            this.Close();
            return;
        }

        await EnsureDefaultsBackedUpAsync();
        await RefreshInstallButtonStateAsync();

        _ = this.DispatcherQueue.TryEnqueue(() =>
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
        });
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        var hWnd = WindowNative.GetWindowHandle(this);
        var isPreview = TunerVariables.Persistent.IsTargetingPreview;
        var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(isPreview, hWnd);

        if (path != null)
        {
            await ContinueInitializationWithPath(path);
        }
        else
        {
            StatusMessage = "No valid Minecraft installation selected";
            this.Close();
        }
    }

    private string EstablishBackupFolder()
    {
        try
        {
            var backupLocation = Path.Combine(ApplicationData.Current.LocalFolder.Path, "RTX_Defaults");
            Directory.CreateDirectory(backupLocation);
            Trace.WriteLine($"RTXW: Backup folder: {backupLocation}");
            return backupLocation;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RTXW: Failed to create backup folder: {ex.Message}");
            return null;
        }
    }

    private async Task EnsureDefaultsBackedUpAsync()
    {
        bool allBackupsPresent =
            File.Exists(BackupLut) && File.Exists(BackupSky) && File.Exists(BackupWater);

        if (allBackupsPresent)
        {
            Trace.WriteLine("RTXW: All default backups already present");
            return;
        }

        Trace.WriteLine("RTXW: Backup(s) missing - checking game files");

        bool allGameFilesPresent =
            File.Exists(DstLut) && File.Exists(DstSky) && File.Exists(DstWater);

        if (allGameFilesPresent)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(BackupLut)) { File.Copy(DstLut, BackupLut, overwrite: false); Trace.WriteLine($"RTXW: Backed up {FnLut}"); }
                    if (!File.Exists(BackupSky)) { File.Copy(DstSky, BackupSky, overwrite: false); Trace.WriteLine($"RTXW: Backed up {FnSky}"); }
                    if (!File.Exists(BackupWater)) { File.Copy(DstWater, BackupWater, overwrite: false); Trace.WriteLine($"RTXW: Backed up {FnWater}"); }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"RTXW: Error during backup: {ex.Message}");
                }
            });
        }
        else
        {
            Trace.WriteLine("RTXW: Game RTX files missing - mending from app assets");
            bool mended = await ReplaceRtxFilesWithElevation(SrcLut, SrcSky, SrcWater);
            Trace.WriteLine(mended ? "RTXW: Game mended" : "RTXW: Mend failed or cancelled");
        }
    }

    private async Task RefreshInstallButtonStateAsync()
    {
        bool installed = await AreOurFilesInstalledAsync();

        _ = this.DispatcherQueue.TryEnqueue(() =>
        {
            if (installed)
            {
                InstallButtonText.Text = "Revert to Defaults";
                InstallButton.Style = null;
            }
            else
            {
                InstallButtonText.Text = "Install";
                InstallButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
            }
        });
    }

    private async Task<bool> AreOurFilesInstalledAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(DstLut) || !File.Exists(SrcLut)) return false;
                if (!File.Exists(DstSky) || !File.Exists(SrcSky)) return false;
                if (!File.Exists(DstWater) || !File.Exists(SrcWater)) return false;
                return HashesMatch(DstLut, SrcLut) && HashesMatch(DstSky, SrcSky) && HashesMatch(DstWater, SrcWater);
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RTXW: Error comparing hashes: {ex.Message}");
            return false;
        }
    }

    private static bool HashesMatch(string pathA, string pathB)
    {
        using var sha = SHA256.Create();
        using var streamA = File.OpenRead(pathA);
        var hashA = sha.ComputeHash(streamA);
        sha.Initialize();
        using var streamB = File.OpenRead(pathB);
        var hashB = sha.ComputeHash(streamB);
        return System.MemoryExtensions.SequenceEqual(
            (System.ReadOnlySpan<byte>)hashA,
            (System.ReadOnlySpan<byte>)hashB);
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;

        try
        {
            bool installed = await AreOurFilesInstalledAsync();

            if (installed)
            {
                if (!File.Exists(BackupLut) || !File.Exists(BackupSky) || !File.Exists(BackupWater))
                {
                    Trace.WriteLine("RTXW: Cannot revert - backup files missing");
                    StatusMessage = "Cannot revert: original game file backups are missing.";
                    return;
                }

                bool success = await ReplaceRtxFilesWithElevation(BackupLut, BackupSky, BackupWater);
                if (success)
                {
                    OperationSuccessful = true;
                    StatusMessage = "Reverted RTX files to game defaults";
                    Trace.WriteLine("RTXW: Reverted to game defaults");
                }
                else
                {
                    Trace.WriteLine("RTXW: Revert cancelled or failed");
                }
            }
            else
            {
                bool success = await ReplaceRtxFilesWithElevation(SrcLut, SrcSky, SrcWater);
                if (success)
                {
                    OperationSuccessful = true;
                    StatusMessage = "Installed Default RTX Modifiers";
                    Trace.WriteLine("RTXW: Files installed");
                }
                else
                {
                    Trace.WriteLine("RTXW: Install cancelled or failed");
                }
            }

            await RefreshInstallButtonStateAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RTXW: Error in InstallButton_Click: {ex.Message}");
        }
        finally
        {
            _ = this.DispatcherQueue.TryEnqueue(() => InstallButton.IsEnabled = true);
        }
    }

    // Convenience wrapper - validates sources, logs all paths, then delegates
    private Task<bool> ReplaceRtxFilesWithElevation(string srcLut, string srcSky, string srcWater)
    {
        Trace.WriteLine("RTXW: ReplaceRtxFilesWithElevation");
        Trace.WriteLine($"  srcLut  ={srcLut}  exists={File.Exists(srcLut)}");
        Trace.WriteLine($"  srcSky  ={srcSky}  exists={File.Exists(srcSky)}");
        Trace.WriteLine($"  srcWater={srcWater}  exists={File.Exists(srcWater)}");
        Trace.WriteLine($"  dstLut  ={DstLut}");
        Trace.WriteLine($"  dstSky  ={DstSky}");
        Trace.WriteLine($"  dstWater={DstWater}");

        if (!File.Exists(srcLut)) { Trace.WriteLine("RTXW: Aborting - srcLut missing"); return Task.FromResult(false); }
        if (!File.Exists(srcSky)) { Trace.WriteLine("RTXW: Aborting - srcSky missing"); return Task.FromResult(false); }
        if (!File.Exists(srcWater)) { Trace.WriteLine("RTXW: Aborting - srcWater missing"); return Task.FromResult(false); }

        var files = new List<(string, string)>
        {
            (srcLut,   DstLut),
            (srcSky,   DstSky),
            (srcWater, DstWater)
        };
        return ReplaceFilesWithElevation(files);
    }

    // Battle-tested core - identical pattern to DLSSSwitcherWindow.ReplaceDllWithElevation
    // but accepts a list so all three files go in one UAC prompt.
    // Uses cmd.exe /c so WaitForExit() tracks the batch process itself,
    // not the UAC broker shell that would return before the copies finish.
    private async Task<bool> ReplaceFilesWithElevation(List<(string sourcePath, string destPath)> filesToReplace)
    {
        try
        {
            return await Task.Run(() =>
            {
                var scriptLines = new List<string> { "@echo off" };
                foreach (var (sourcePath, destPath) in filesToReplace)
                {
                    scriptLines.Add("copy /Y \"" + sourcePath + "\" \"" + destPath + "\" >nul 2>&1");
                }
                scriptLines.Add("exit %ERRORLEVEL%");

                var batchScript = string.Join("\r\n", scriptLines);
                var tempBatchPath = Path.Combine(Path.GetTempPath(), $"rtx_defaults_{Guid.NewGuid():N}.bat");
                File.WriteAllText(tempBatchPath, batchScript);

                Trace.WriteLine($"RTXW: Batch written to {tempBatchPath}");
                Trace.WriteLine($"RTXW: Batch contents:\n{batchScript}");

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"" + tempBatchPath + "\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        process.WaitForExit();
                        Trace.WriteLine($"RTXW: Batch exit code: {process.ExitCode}");
                        return process.ExitCode == 0;
                    }

                    Trace.WriteLine("RTXW: Process.Start returned null");
                    return false;
                }
                finally
                {
                    try
                    {
                        Thread.Sleep(300);
                        if (File.Exists(tempBatchPath)) File.Delete(tempBatchPath);
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RTXW: Error in ReplaceFilesWithElevation: {ex.Message}");
            return false;
        }
    }
}
