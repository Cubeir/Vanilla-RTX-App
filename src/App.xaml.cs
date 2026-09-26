using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.FileActivation;
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
        _ = Teletexts.TriggerUpdateAsync(); // Silent Teletext Update, hopefully by the time the startup sequence is finished, we have new Teletexts to show!


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
            // Before the wake signal, never after - see HandOffToRunningInstance.
            FileActivationRouter.HandOffToRunningInstance();

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

        // A link that can run without a window (e.g. launching the game from a desktop
        // shortcut) runs here, before MainWindow exists, and the app never appears. The wake
        // event already exists so a second launch arriving meanwhile still finds us: its
        // signal stays set until the listener below consumes it, and the only thing that
        // changes is that we then start up rather than exit.
        if (await FileActivationRouter.TryRunLaunchLinkWithoutWindowAsync())
        {
            if (!_wakeEvent.WaitOne(0))
            {
                Exit();
                return;
            }
            _wakeEvent.Set(); // put back what WaitOne just consumed, for the listener
        }

        // Brief delay before Activate() to allow InitializeComponent() and lamp animators
        // to finish rendering before the window becomes visible, preventing a black background briefly appearing or splash images not loading in time.
        _window = new MainWindow(); // -> This kicks off the stuff in MainWindow actually running, which also calls for XAML to be initialized
        await Task.Delay(175); // A delay ensures the xaml is constructed before window tries to appear.
        _window.Activate();

        // Only once MainWindow.Instance exists: the handler enqueues on its dispatcher, and a
        // wake consumed before then would be dropped along with whatever it handed off. The
        // event is AutoReset, so a wake that arrived earlier is still waiting here.
        _ = Task.Run(() =>
        {
            while (_wakeEvent.WaitOne())
            {
                // Raising a window is the router's call, not this handler's: a wake carrying
                // a file is answered by whichever window imports it, which is not always
                // MainWindow.
                MainWindow.Instance?.DispatcherQueue.TryEnqueue(async () =>
                {
                    await FileActivationRouter.HandleWakeAsync();
                });
            }
        });

        _ = FileActivationRouter.RouteLaunchAsync();
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
                $"{TraceManager.GetRecentExceptions()}\n" +
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

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

        Trace.WriteLine("TraceManager initialized");
    }

    // ── Recent exceptions ─────────────────────────────────────────────────────

    private sealed class ExceptionRecord
    {
        public DateTime Timestamp;
        public int ThreadId;
        public required string Key;
        public required string Summary;
        public required string Stack;
        public int Repeats;
    }

    private const int MaxExceptionRecords = 40;
    private const int MaxStackChars = 4000;
    private static readonly LinkedList<ExceptionRecord> _exceptions = new();
    private static readonly object _exceptionsLock = new();
    [ThreadStatic] private static bool _inFirstChanceHandler;

    /// <summary>
    /// Keeps the last <see cref="MaxExceptionRecords"/> exceptions thrown anywhere in the
    /// process, with the call stack that threw them, for the debug report and the crash log.
    ///
    /// <para><b>This is where the report's stack traces come from, and it has to be first-chance.</b>
    /// The app catches almost everything it throws and logs <c>ex.Message</c> to Trace, so by the
    /// time a user presses "Copy debug logs" the stack of the failure they are reporting is gone.
    /// A stack captured at report time is only ever the button's own click handler. And
    /// <c>ex.StackTrace</c> is no use here either: it is built while the exception unwinds, so at
    /// first chance it holds the throwing frame and nothing above it - hence
    /// <c>new StackTrace(1)</c>, which is the full call stack at the throw.</para>
    ///
    /// <para><b>Cost is per throw, and nothing when nothing throws.</b> A stack walk is tens of
    /// microseconds; the heaviest thrower is a pack scan retrying malformed JSON, a few hundred
    /// times at most. No file/line info is requested, which is the expensive half and needs PDBs
    /// a Release build doesn't ship anyway - method names survive trimming and are enough.</para>
    ///
    /// <para>Cancellation is skipped: it is how every overlay stops its own work, not a failure.
    /// A throw identical to the one before it (same type, message and stack - a retry loop)
    /// bumps a counter instead of evicting everything else from the buffer. The handler must
    /// never throw, and is guarded against re-entry because anything it calls could itself throw
    /// first-chance.</para>
    /// </summary>
    private static void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (_inFirstChanceHandler || e.Exception is OperationCanceledException) return;
        _inFirstChanceHandler = true;
        try
        {
            var stack = new StackTrace(1, false).ToString();
            if (stack.Length > MaxStackChars) stack = stack[..MaxStackChars] + "   ...";

            var ex = e.Exception;
            var summary = $"{ex.GetType().FullName} (0x{ex.HResult:X8}): {ex.Message}";
            var key = summary + stack;

            lock (_exceptionsLock)
            {
                if (_exceptions.Last?.Value is { } last && last.Key == key)
                {
                    last.Repeats++;
                    last.Timestamp = DateTime.Now;
                    return;
                }

                _exceptions.AddLast(new ExceptionRecord
                {
                    Timestamp = DateTime.Now,
                    ThreadId = Environment.CurrentManagedThreadId,
                    Key = key,
                    Summary = summary,
                    Stack = stack,
                });
                if (_exceptions.Count > MaxExceptionRecords)
                    _exceptions.RemoveFirst();
            }
        }
        catch { }
        finally
        {
            _inFirstChanceHandler = false;
        }
    }

    /// <summary>The recorded exceptions, oldest first, as a report section.</summary>
    public static string GetRecentExceptions()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"===== Recent Exceptions (last {MaxExceptionRecords}, handled or not, cancellations excluded)");
        lock (_exceptionsLock)
        {
            if (_exceptions.Count == 0)
                sb.AppendLine("(none)");

            foreach (var r in _exceptions)
            {
                sb.Append($"[{r.Timestamp:HH:mm:ss.fff}] [T{r.ThreadId}] {r.Summary}");
                sb.AppendLine(r.Repeats > 0 ? $"  (x{r.Repeats + 1}, last at the time shown)" : "");
                sb.AppendLine(r.Stack.TrimEnd());
                sb.AppendLine();
            }
        }
        return sb.ToString();
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
