using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Vanilla_RTX_App.Core;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using WinUIEx;

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
    private static EventWaitHandle? _wakeEvent = null;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
        TraceManager.Initialize();
        _ = OnlineTexts.TriggerUpdateAsync(); // Silent PSA Update, hopefully by the time the startup sequence is finished, we have new PSAs to show!


        // 1. Catches unhandled exceptions on the UI thread from any window
        this.UnhandledException += (s, e) =>
        {
            WriteCrashLog("UI Thread", $"[{e.Exception.GetType().FullName} / 0x{e.Exception.HResult:X8}] {e.Message}", e.Exception.ToString());
            // intentionally not setting e.Handled = true
            // let it crash naturally so WER still gets the dump
        };

        // 2. Catches exceptions escaping async void after an await,
        // and anything thrown on the UI thread that XAML doesn't intercept
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            WriteCrashLog("Unobserved Task", e.Exception.Message, e.Exception.ToString());
            e.SetObserved(); // prevents process termination for tasks,
                             // since we've logged it ourselves
        };

        // 3. Catches exceptions on background threads, Thread.Start, etc.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            WriteCrashLog("Background Thread", ex?.Message ?? "Unknown", ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "No details");
            // can't prevent termination here, but log is written
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        bool isNewInstance;
        _mutex = new Mutex(true, GetUniqueName(), out isNewInstance);

        if (!isNewInstance)
        {
            // This process is about to hand off to the one already running - if a .mcpack
            // was what launched it, that file would otherwise be lost the moment we Exit().
            // Written before the wake signal, never after: the existing instance only checks
            // for this file once it wakes up, so the order here is what guarantees it sees it.
            var incomingFiles = GetActivationFilePaths();
            if (incomingFiles.Count > 0)
                WritePendingImportFile(incomingFiles);

            // Signal the existing instance to bring itself to front
            if (EventWaitHandle.TryOpenExisting($"{GetUniqueName()}_wake", out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            Exit();
            return;
        }

        // Create the wake event for this instance to listen on
        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"{GetUniqueName()}_wake");
        _ = Task.Run(() =>
        {
            while (_wakeEvent.WaitOne())
            {
                MainWindow.Instance?.DispatcherQueue.TryEnqueue(async () =>
                {
                    MainWindow.Instance.Restore();            // un-minimizes/un-maximizes, WinUIEx
                    MainWindow.Instance.SetForegroundWindow(); // brings to foreground, WinUIEx

                    // A second launch that lost the race for the mutex leaves its
                    // .mcpack/.rtpack paths here rather than its files - see the write above.
                    var pendingFiles = ConsumePendingImportFile();
                    if (pendingFiles.Count > 0)
                        await RouteIncomingFilesAsync(pendingFiles);
                });
            }
        });

        // Brief delay before Activate() to allow InitializeComponent() and lamp animators
        // to finish rendering before the window becomes visible, preventing a black background briefly appearing or splash images not loading in time.
        _window = new MainWindow(); // -> This kicks off the stuff in MainWindow actually running, which also calls for XAML to be initialized
        await Task.Delay(175); // A delay ensures the xaml is constructed before window tries to appear.
        _window.Activate();

        // Cold launch via .mcpack/.rtpack ("Open with", double-click) rather than the normal
        // icon - this process won the mutex outright, so its own activation args carry the
        // files directly; no hand-off file involved.
        var launchFiles = GetActivationFilePaths();
        if (launchFiles.Count > 0)
            _ = RouteIncomingFilesAsync(launchFiles);
    }

    // ── .mcpack / .rtpack file activation ────────────────────────────────────

    /// <summary>
    /// Splits incoming activation paths by extension and hands each group to the window
    /// method that owns that file type - .mcpack/.zip/.mcaddon to
    /// MainWindow.ImportPackFilesAsync, .rtpack to
    /// MainWindow.ImportBetterRTXPresetFilesAsync. Both file-type associations funnel
    /// through here, whether the launch was cold or handed off from a losing second
    /// instance (see ConsumePendingImportFile), so a mixed selection - unlikely, but Explorer
    /// permits it - still routes correctly instead of one type winning outright.
    /// </summary>
    private static async Task RouteIncomingFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || MainWindow.Instance == null) return;

        var rtpackFiles = paths
            .Where(p => Path.GetExtension(p).Equals(".rtpack", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var packFiles = paths.Except(rtpackFiles).ToList();

        if (packFiles.Count > 0)
            await MainWindow.Instance.ImportPackFilesAsync(packFiles);

        if (rtpackFiles.Count > 0)
            await MainWindow.Instance.ImportBetterRTXPresetFilesAsync(rtpackFiles);
    }

    /// <summary>
    /// Reads the paths this process was actually launched with, if it was a file activation
    /// ("Open with", double-click on a .mcpack or .rtpack) - empty otherwise, including for
    /// the ordinary icon-launch case. The classic LaunchActivatedEventArgs OnLaunched
    /// receives doesn't carry file activation data for a full-trust packaged app; that lives
    /// on AppInstance.GetCurrent's own activation args regardless of which OnLaunched
    /// overload fired.
    /// </summary>
    private static List<string> GetActivationFilePaths()
    {
        try
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activationArgs?.Kind != ExtendedActivationKind.File) return new List<string>();
            if (activationArgs.Data is not FileActivatedEventArgs fileArgs) return new List<string>();

            return fileArgs.Files
                .OfType<IStorageFile>()
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FileActivation] Failed to read activation args: {ex.Message}");
            return new List<string>();
        }
    }

    private static string PendingImportFilePath =>
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "pending_pack_import.txt");

    /// <summary>
    /// One path per line, .mcpack and .rtpack mixed together freely - RouteIncomingFilesAsync
    /// is what sorts them back out by extension on the reading side. Plain text rather than
    /// JSON is a deliberate choice here, not laziness: Release publishes trimmed, and
    /// JsonSerializer's generic overloads need a source-generated context to survive that
    /// (see AlchitexJsonContext for the pattern and what happens without it). A flat file of
    /// paths needs none of that, and a Windows path can never itself contain a newline.
    /// </summary>
    private static void WritePendingImportFile(List<string> paths)
    {
        try { File.WriteAllLines(PendingImportFilePath, paths); }
        catch (Exception ex) { Trace.WriteLine($"[FileActivation] Failed to write hand-off file: {ex.Message}"); }
    }

    private static List<string> ConsumePendingImportFile()
    {
        try
        {
            if (!File.Exists(PendingImportFilePath)) return new List<string>();

            var paths = File.ReadAllLines(PendingImportFilePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            File.Delete(PendingImportFilePath);
            return paths;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FileActivation] Failed to read hand-off file: {ex.Message}");
            return new List<string>();
        }
    }

    public static void WriteCrashLog(string source, string message, string detail)
    {
        try
        {
            var logPath = Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                "last_session_crash_log.txt");

            File.AppendAllText(logPath,
                $"=== Crash Report ===\n" +
                $"Version:   {EnvironmentVariables.appVersion ?? "unknown"}\n" +
                $"Source:    {source}\n" +
                $"Time:      {DateTime.Now}\n" +
                $"Message:   {message}\n" +
                $"Detail:\n{detail}\n\n" +
                $"{TraceManager.GetAllTraceLogs()}\n\n");
        }
        catch { /* we're truly fucked then */ }
    }

    public static string GetUniqueName()
    {
        try
        {
            var family = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            var idx = family.LastIndexOf('_');
            var suffix = (idx >= 0 && idx < family.Length - 1)
                ? family[(idx + 1)..]
                : family;
            return $"vrtxapp_{suffix}";
        }
        catch
        {
            return "vanilla_rtx_app";
        }
    }
    public static Windows.ApplicationModel.PackageVersion GetPackageVersion()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.Version;
        }
        catch
        {
            Trace.WriteLine("[GetAppVersion] Failed.");
            return new Windows.ApplicationModel.PackageVersion { Major = 0, Minor = 0, Build = 0, Revision = 0 };
        }
    }
}




/// <summary>
/// Custom TraceListener that captures all Trace.WriteLine calls
/// </summary>
public class InMemoryTraceListener : TraceListener
{
    private readonly ConcurrentQueue<TraceEntry> _entries = new();
    private readonly int _maxEntries;
    private int _count;

    public InMemoryTraceListener(int maxEntries = 1000)
    {
        _maxEntries = maxEntries;
    }

    public override void Write(string? message)
    {
        // Usually not used, but implement for completeness
        WriteLine(message);
    }

    public override void WriteLine(string? message)
    {
        var entry = new TraceEntry
        {
            Timestamp = DateTime.Now,
            Message = message,
            ThreadId = Environment.CurrentManagedThreadId
        };

        _entries.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > _maxEntries)
        {
            if (_entries.TryDequeue(out _))
                Interlocked.Decrement(ref _count);
        }
    }

    public string GetAllEntries()
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== Trace Logs");

        foreach (var entry in _entries)
        {
            sb.AppendLine($"[{entry.Timestamp:HH:mm:ss.fff}] [T{entry.ThreadId}] {entry.Message}");
        }

        return sb.ToString();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    private class TraceEntry
    {
        public DateTime Timestamp { get; set; }
        public string? Message { get; set; }
        public int ThreadId { get; set; }
    }
}
public static class TraceManager
{
    private static InMemoryTraceListener? _listener;

    public static void Initialize()
    {
        // Enable if we don't want debugger output...
        // Trace.Listeners.Clear();

        _listener = new InMemoryTraceListener(maxEntries: 25000);
        Trace.Listeners.Add(_listener);

        Trace.WriteLine("TraceManager initialized");
    }

    public static string GetAllTraceLogs()
    {
        return _listener?.GetAllEntries() ?? "Trace logging not initialized";
    }

    public static void ClearTraceLogs()
    {
        _listener?.Clear();
    }
}
