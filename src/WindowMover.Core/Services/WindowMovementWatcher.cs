using System.Collections.Concurrent;
using System.Diagnostics;
using WindowMover.Core.Models;
using WindowMover.Core.Native;

namespace WindowMover.Core.Services;

/// <summary>
/// Hooks into Win32 events to detect when a user finishes moving/resizing a window,
/// then reports which monitor the window landed on.
/// Only fires for user-initiated moves: tracks MOVESIZESTART→MOVESIZEEND pairs
/// (programmatic SetWindowPos calls do not generate these events) and exposes a
/// suppression flag for additional safety during rule application.
/// </summary>
public class WindowMovementWatcher : IDisposable
{
    private readonly MonitorIdentifier _monitorIdentifier;
    private IntPtr _startHookHandle;
    private IntPtr _endHookHandle;
    private User32.WinEventDelegate? _hookDelegate;
    private bool _disposed;

    // Windows that the user is actively moving (saw MOVESIZESTART but not yet MOVESIZEEND)
    private readonly ConcurrentDictionary<IntPtr, bool> _activeUserMoves = new();

    /// <summary>
    /// When true, all move events are ignored. Set this before programmatic
    /// window moves (ApplyRules, setup changes) and clear it afterward.
    /// </summary>
    public bool Suppressed { get; set; }

    /// <summary>
    /// Raised when the user finishes moving a window to a (possibly different) monitor.
    /// </summary>
    public event EventHandler<WindowMovedEventArgs>? WindowMoved;

    public WindowMovementWatcher(MonitorIdentifier monitorIdentifier)
    {
        _monitorIdentifier = monitorIdentifier;
    }

    /// <summary>
    /// Starts listening for window move/resize events.
    /// Must be called from a thread with a message pump (UI thread).
    /// </summary>
    public void Start()
    {
        if (_startHookHandle != IntPtr.Zero)
            return;

        // Must keep delegate alive to prevent GC collection
        _hookDelegate = OnWinEvent;

        // Hook both START and END so we can pair them
        _startHookHandle = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_MOVESIZESTART,
            User32.EVENT_SYSTEM_MOVESIZESTART,
            IntPtr.Zero,
            _hookDelegate,
            0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        _endHookHandle = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_MOVESIZEEND,
            User32.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero,
            _hookDelegate,
            0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>
    /// Stops listening for window events.
    /// </summary>
    public void Stop()
    {
        if (_startHookHandle != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_startHookHandle);
            _startHookHandle = IntPtr.Zero;
        }
        if (_endHookHandle != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_endHookHandle);
            _endHookHandle = IntPtr.Zero;
        }
        _hookDelegate = null;
        _activeUserMoves.Clear();
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero)
            return;

        if (eventType == User32.EVENT_SYSTEM_MOVESIZESTART)
        {
            // User has grabbed a window — record it.
            // Programmatic SetWindowPos never fires this event.
            if (!Suppressed)
                _activeUserMoves[hwnd] = true;
            return;
        }

        if (eventType == User32.EVENT_SYSTEM_MOVESIZEEND)
        {
            // Only process if we saw the matching START for this window
            if (!_activeUserMoves.TryRemove(hwnd, out _))
                return;

            if (Suppressed)
                return;

            if (!User32.IsWindowVisible(hwnd))
                return;

            ProcessWindowMoveEnd(hwnd);
        }
    }

    private void ProcessWindowMoveEnd(IntPtr hwnd)
    {
        try
        {
            User32.GetWindowThreadProcessId(hwnd, out var processId);
            var process = Process.GetProcessById((int)processId);

            var hMonitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
            var monitorInfo = User32.MONITORINFO.Default;
            User32.GetMonitorInfo(hMonitor, ref monitorInfo);

            var monitors = _monitorIdentifier.GetConnectedMonitors();
            var targetMonitor = FindMonitorByBounds(monitors, monitorInfo);

            if (targetMonitor != null)
            {
                string? exePath = null;
                try { exePath = process.MainModule?.FileName; } catch { }

                WindowMoved?.Invoke(this, new WindowMovedEventArgs(
                    hwnd,
                    process.ProcessName,
                    exePath,
                    targetMonitor));
            }
        }
        catch (ArgumentException)
        {
            // Process exited
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WindowMovementWatcher error: {ex.Message}");
        }
    }

    private static MonitorInfo? FindMonitorByBounds(
        IReadOnlyList<MonitorInfo> monitors, User32.MONITORINFO nativeInfo)
    {
        foreach (var m in monitors)
        {
            if (m.Bounds.Left == nativeInfo.rcMonitor.Left &&
                m.Bounds.Top == nativeInfo.rcMonitor.Top &&
                m.Bounds.Width == nativeInfo.rcMonitor.Width &&
                m.Bounds.Height == nativeInfo.rcMonitor.Height)
            {
                return m;
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}

public class WindowMovedEventArgs : EventArgs
{
    public IntPtr WindowHandle { get; }
    public string ProcessName { get; }
    public string? ExecutablePath { get; }
    public MonitorInfo TargetMonitor { get; }

    public WindowMovedEventArgs(IntPtr windowHandle, string processName,
        string? executablePath, MonitorInfo targetMonitor)
    {
        WindowHandle = windowHandle;
        ProcessName = processName;
        ExecutablePath = executablePath;
        TargetMonitor = targetMonitor;
    }
}
