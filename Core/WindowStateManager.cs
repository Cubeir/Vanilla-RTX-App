using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Window state manager
/// </summary>
public class WindowStateManager : IDisposable
{
    private AppWindow? _appWindow;
    private Window? _window;
    private Action<string>? _logAction;
    private readonly bool _enableLogging;

    private const int MIN_WINDOW_WIDTH = 400;
    private const int MIN_WINDOW_HEIGHT = 300;
    private const int DEFAULT_WINDOW_WIDTH = 1055;
    private const int DEFAULT_WINDOW_HEIGHT = 555;

    private double? _cachedDpiScale;
    private const double DEFAULT_DPI = 96.0;

    public WindowStateManager(Window window, bool enableLogging = false, Action<string>? logAction = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _enableLogging = enableLogging;
        _logAction = logAction;

        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow == null)
            {
                throw new InvalidOperationException("Failed to get AppWindow from WindowId");
            }

            _window.Closed += OnWindowClosed;
        }
        catch (Exception ex)
        {
            Log($"WindowStateManager initialization failed: {ex.Message}");
            throw;
        }
    }

    private void Log(string message)
    {
        if (_enableLogging && _logAction != null)
        {
            _logAction.Invoke($"[WindowStateManager] {message}");
        }
    }

    public void ApplySavedStateOrDefaults(SizeInt32? defaultSize = null, PointInt32? defaultPosition = null)
    {
        if (_appWindow == null)
        {
            Log("AppWindow is null, cannot apply window state");
            return;
        }

        var logicalDefaultSize = new SizeInt32(DEFAULT_WINDOW_WIDTH, DEFAULT_WINDOW_HEIGHT);
        var physicalDefaultSize = LogicalToPhysical(logicalDefaultSize);

        var savedState = LoadWindowState();
        var sizeToUse = physicalDefaultSize;
        PointInt32? positionToUse = null;

        if (savedState.HasValue)
        {
            var state = savedState.Value;
            Log($"Loaded saved state: {state.Width}x{state.Height} at ({state.X},{state.Y})");

            if (state.Width >= MIN_WINDOW_WIDTH && state.Height >= MIN_WINDOW_HEIGHT)
            {
                var savedLogicalSize = new SizeInt32(state.Width, state.Height);
                sizeToUse = LogicalToPhysical(savedLogicalSize);
                Log($"Using saved size: {state.Width}x{state.Height} (Physical: {sizeToUse.Width}x{sizeToUse.Height})");
            }
            else
            {
                Log($"Saved size invalid ({state.Width}x{state.Height}), using default: {DEFAULT_WINDOW_WIDTH}x{DEFAULT_WINDOW_HEIGHT}");
                sizeToUse = physicalDefaultSize;
            }

            var savedLogicalPosition = new PointInt32(state.X, state.Y);
            var savedPhysicalPosition = LogicalToPhysical(savedLogicalPosition);
       

            if (IsPositionOnActiveMonitor(savedPhysicalPosition, sizeToUse))
            {
                positionToUse = savedPhysicalPosition;
                Log($"Using saved position: ({state.X},{state.Y}) (Physical: {positionToUse.Value.X},{positionToUse.Value.Y})");
            }
            else
            {
                Log($"Saved position ({state.X},{state.Y}) not on active monitor, will center window");
            }
        }
        else
        {
            Log("No saved state found, using defaults");
        }

        try
        {
            _appWindow.Resize(sizeToUse);
            Log($"Applied size: {sizeToUse.Width}x{sizeToUse.Height}");

            if (positionToUse.HasValue)
            {
                _appWindow.Move(positionToUse.Value);
                Log($"Applied position: {positionToUse.Value.X},{positionToUse.Value.Y}");
            }
            else if (defaultPosition.HasValue)
            {
                var physicalDefaultPosition = LogicalToPhysical(defaultPosition.Value);
                _appWindow.Move(physicalDefaultPosition);
                Log($"Applied default position: {physicalDefaultPosition.X},{physicalDefaultPosition.Y}");
            }
            else
            {
                CenterWindow(sizeToUse);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to apply window state: {ex.Message}");
            try
            {
                _appWindow.Resize(physicalDefaultSize);
                CenterWindow(physicalDefaultSize);
                Log("Applied fallback positioning");
            }
            catch (Exception fallbackEx)
            {
                Log($"Fallback positioning also failed: {fallbackEx.Message}");
            }
        }
    }

    private double GetDpiScale()
    {
        if (_cachedDpiScale.HasValue)
            return _cachedDpiScale.Value;

        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var dpi = GetDpiForWindow(hWnd);
            _cachedDpiScale = dpi / DEFAULT_DPI;
            Log($"DPI scale factor: {_cachedDpiScale.Value:F2} (DPI: {dpi})");
            return _cachedDpiScale.Value;
        }
        catch (Exception ex)
        {
            Log($"Failed to get DPI, using default scale: {ex.Message}");
            _cachedDpiScale = 1.0;
            return 1.0;
        }
    }

    private SizeInt32 LogicalToPhysical(SizeInt32 logicalSize)
    {
        var scale = GetDpiScale();
        return new SizeInt32(
            (int)Math.Round(logicalSize.Width * scale),
            (int)Math.Round(logicalSize.Height * scale)
        );
    }

    private PointInt32 LogicalToPhysical(PointInt32 logicalPoint)
    {
        var scale = GetDpiScale();
        return new PointInt32(
            (int)Math.Round(logicalPoint.X * scale),
            (int)Math.Round(logicalPoint.Y * scale)
        );
    }

    private SizeInt32 PhysicalToLogical(SizeInt32 physicalSize)
    {
        var scale = GetDpiScale();
        return new SizeInt32(
            (int)Math.Round(physicalSize.Width / scale),
            (int)Math.Round(physicalSize.Height / scale)
        );
    }

    private PointInt32 PhysicalToLogical(PointInt32 physicalPoint)
    {
        var scale = GetDpiScale();
        return new PointInt32(
            (int)Math.Round(physicalPoint.X / scale),
            (int)Math.Round(physicalPoint.Y / scale)
        );
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONULL = 0x00000000;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>
    /// Only restore position if it's on the SAME monitor where the mouse currently is
    /// Otherwise center on active monitor. This handles all edge cases perfectly.
    /// </summary>
    private bool IsPositionOnActiveMonitor(PointInt32 position, SizeInt32 size)
    {
        try
        {
            // Get the monitor where the mouse cursor currently is (this is the ACTIVE monitor)
            POINT cursorPos;
            if (!GetCursorPos(out cursorPos))
            {
                Log("Failed to get cursor position");
                return false;
            }

            var activeMonitor = MonitorFromPoint(cursorPos, MONITOR_DEFAULTTONEAREST);
            if (activeMonitor == IntPtr.Zero)
            {
                Log("Failed to get active monitor from cursor");
                return false;
            }

            // Get bounds of the active monitor
            var activeMonitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(activeMonitor, ref activeMonitorInfo))
            {
                Log("Failed to get active monitor info");
                return false;
            }

            // Check if the saved window position is on the same monitor
            // We check the center point of the window
            var windowCenter = new POINT
            {
                X = position.X + size.Width / 2,
                Y = position.Y + size.Height / 2
            };

            var savedMonitor = MonitorFromPoint(windowCenter, MONITOR_DEFAULTTONEAREST);

            if (savedMonitor == IntPtr.Zero)
            {
                Log($"Saved position ({position.X},{position.Y}) - failed to get monitor");
                return false;
            }

            // Get bounds of the saved monitor
            var savedMonitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(savedMonitor, ref savedMonitorInfo))
            {
                Log("Failed to get saved position monitor info");
                return false;
            }

            // Compare the monitor bounds to see if they're the same physical monitor
            bool isSameMonitor =
                activeMonitorInfo.rcMonitor.Left == savedMonitorInfo.rcMonitor.Left &&
                activeMonitorInfo.rcMonitor.Top == savedMonitorInfo.rcMonitor.Top &&
                activeMonitorInfo.rcMonitor.Right == savedMonitorInfo.rcMonitor.Right &&
                activeMonitorInfo.rcMonitor.Bottom == savedMonitorInfo.rcMonitor.Bottom;

            if (isSameMonitor)
            {
                Log($"✅ Saved position IS on active monitor (cursor at {cursorPos.X},{cursorPos.Y}, monitor bounds {activeMonitorInfo.rcMonitor.Left},{activeMonitorInfo.rcMonitor.Top} to {activeMonitorInfo.rcMonitor.Right},{activeMonitorInfo.rcMonitor.Bottom})");
                return true;
            }
            else
            {
                Log($"❌ Saved position is on DIFFERENT monitor. Active: ({activeMonitorInfo.rcMonitor.Left},{activeMonitorInfo.rcMonitor.Top} {activeMonitorInfo.rcMonitor.Right - activeMonitorInfo.rcMonitor.Left}x{activeMonitorInfo.rcMonitor.Bottom - activeMonitorInfo.rcMonitor.Top}), Saved: ({savedMonitorInfo.rcMonitor.Left},{savedMonitorInfo.rcMonitor.Top} {savedMonitorInfo.rcMonitor.Right - savedMonitorInfo.rcMonitor.Left}x{savedMonitorInfo.rcMonitor.Bottom - savedMonitorInfo.rcMonitor.Top})");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log($"Monitor validation failed: {ex.Message}");
            return false;
        }
    }

    private void CenterWindow(SizeInt32 windowSize)
    {
        try
        {
            var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;

            var centerX = workArea.X + (workArea.Width - windowSize.Width) / 2;
            var centerY = workArea.Y + (workArea.Height - windowSize.Height) / 2;

            _appWindow.Move(new PointInt32(centerX, centerY));
            Log($"Centered window at: {centerX},{centerY}");
        }
        catch (Exception ex)
        {
            Log($"Centering failed: {ex.Message}");
        }
    }

    private struct WindowState
    {
        public int X
        {
            get; set;
        }
        public int Y
        {
            get; set;
        }
        public int Width
        {
            get; set;
        }
        public int Height
        {
            get; set;
        }
    }

    private WindowState? LoadWindowState()
    {
        try
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var values = settings.Values;

            if (values.ContainsKey("WindowX") && values.ContainsKey("WindowY") &&
                values.ContainsKey("WindowWidth") && values.ContainsKey("WindowHeight"))
            {
                var state = new WindowState
                {
                    X = (int)values["WindowX"],
                    Y = (int)values["WindowY"],
                    Width = (int)values["WindowWidth"],
                    Height = (int)values["WindowHeight"]
                };

                Log($"Loaded state from storage: {state.Width}x{state.Height} at ({state.X},{state.Y})");
                return state;
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to load window state: {ex.Message}");
        }

        return null;
    }

    private void SaveWindowState()
    {
        try
        {
            if (_appWindow == null)
            {
                Log("Cannot save - AppWindow is null");
                return;
            }

            var currentSize = _appWindow.Size;
            var currentPosition = _appWindow.Position;

            var logicalSize = PhysicalToLogical(currentSize);
            var logicalPosition = PhysicalToLogical(currentPosition);

            if (logicalSize.Width < MIN_WINDOW_WIDTH || logicalSize.Height < MIN_WINDOW_HEIGHT)
            {
                Log($"Not saving - size too small: {logicalSize.Width}x{logicalSize.Height}");
                return;
            }

            // Only save if on active monitor (where mouse is)
            if (!IsPositionOnActiveMonitor(currentPosition, currentSize))
            {
                Log($"Not saving - position ({logicalPosition.X},{logicalPosition.Y}) not on active monitor");
                return;
            }

            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var values = settings.Values;

            values["WindowX"] = logicalPosition.X;
            values["WindowY"] = logicalPosition.Y;
            values["WindowWidth"] = logicalSize.Width;
            values["WindowHeight"] = logicalSize.Height;

            Log($"Saved state: {logicalSize.Width}x{logicalSize.Height} at ({logicalPosition.X},{logicalPosition.Y})");
        }
        catch (Exception ex)
        {
            Log($"Failed to save window state: {ex.Message}");
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        SaveWindowState();

        if (_window != null)
        {
            _window.Closed -= OnWindowClosed;
        }
    }

    public void Dispose()
    {
        if (_window != null)
        {
            _window.Closed -= OnWindowClosed;
            _window = null;
        }

        _appWindow = null;
        _logAction = null;
        _cachedDpiScale = null;
    }

    public void SaveCurrentState() => SaveWindowState();

    public bool HasSavedState() => LoadWindowState().HasValue;

    public void ClearSavedState()
    {
        try
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var values = settings.Values;

            values.Remove("WindowX");
            values.Remove("WindowY");
            values.Remove("WindowWidth");
            values.Remove("WindowHeight");

            Log("Cleared saved window state");
        }
        catch (Exception ex)
        {
            Log($"Failed to clear saved state: {ex.Message}");
        }
    }

    public void RefreshDpiCache()
    {
        _cachedDpiScale = null;
        Log("DPI cache refreshed");
    }
}