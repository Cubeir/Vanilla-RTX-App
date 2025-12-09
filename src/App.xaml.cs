using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Vanilla_RTX_App.Modules;


// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Vanilla_RTX_App;
/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private static Mutex? _mutex = null;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        bool isNewInstance;
        _mutex = new Mutex(true, $"VanillaRTXTuner{Helpers.GetCacheFolderName()}", out isNewInstance);

        if (!isNewInstance)
        {
            BringExistingWindowToFront();

            // then xit without creating any new windows
            Exit();
            return;
        }

        // Continue with app initialization only if this is a new instance
        _window = new MainWindow();
        _window.Activate();
    }

    // Clean up mutex when app exits
    ~App()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

    public static void CleanupMutex()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

    private void BringExistingWindowToFront()
    {
        // Find the existing window process
        var processes = System.Diagnostics.Process.GetProcessesByName("Vanilla RTX App");
        foreach (var process in processes)
        {
            if (process.Id != Environment.ProcessId)
            {
                // Bring the existing window to foreground
                ShowWindow(process.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(process.MainWindowHandle);
                break;
            }
        }
    }

    // Windows API
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;
}