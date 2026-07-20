using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Vanilla_RTX_App.Core;
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
                MainWindow.Instance?.DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.Instance.Restore();            // un-minimizes/un-maximizes, WinUIEx
                    MainWindow.Instance.SetForegroundWindow(); // brings to foreground, WinUIEx
                });
            }
        });

        // Brief delay before Activate() to allow InitializeComponent() and lamp animators
        // to finish rendering before the window becomes visible, preventing a black background briefly appearing or splash images not loading in time.
        _window = new MainWindow(); // -> This kicks off the stuff in MainWindow actually running, which also calls for XAML to be initialized
        await Task.Delay(175); // A delay ensures the xaml is constructed before window tries to appear.
        _window.Activate();
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
                $"Version:   {TunerVariables.appVersion ?? "unknown"}\n" +
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
