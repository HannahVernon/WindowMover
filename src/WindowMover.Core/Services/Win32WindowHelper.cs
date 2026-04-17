using System.Diagnostics;
using WindowMover.Core.Native;

namespace WindowMover.Core.Services;

/// <summary>
/// Safe wrappers around Win32 window manipulation APIs.
/// Checks for hung/invalid windows before interacting, and uses
/// SendMessageTimeout to avoid indefinite blocking after hibernate/resume.
/// </summary>
public static class Win32WindowHelper
{
    /// <summary>
    /// Timeout in milliseconds for SendMessageTimeout probes (3 seconds).
    /// </summary>
    private const uint ProbeTimeoutMs = 3000;

    /// <summary>
    /// Raised when a hung or unresponsive window is detected.
    /// The string contains a human-readable description of the window.
    /// </summary>
    public static event EventHandler<HungWindowEventArgs>? HungWindowDetected;

    /// <summary>
    /// Checks whether the target window is valid and responsive.
    /// Returns false if the window handle is invalid, the window is hung,
    /// or the window does not respond to a WM_NULL probe within the timeout.
    /// Logs details and raises <see cref="HungWindowDetected"/> on failure.
    /// </summary>
    public static bool IsWindowResponsive(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return false;

        if (!User32.IsWindow(hWnd))
            return false;

        // Fast check: Win32 hung-app detection
        if (User32.IsHungAppWindow(hWnd))
        {
            ReportHungWindow(hWnd, "IsHungAppWindow returned true");
            return false;
        }

        // Probe: send WM_NULL with timeout to verify the message pump is alive
        nint result;
        var sent = User32.SendMessageTimeout(
            hWnd,
            User32.WM_NULL,
            0,
            0,
            User32.SMTO_ABORTIFHUNG | User32.SMTO_BLOCK,
            ProbeTimeoutMs,
            out result);

        if (sent == 0)
        {
            ReportHungWindow(hWnd, "SendMessageTimeout(WM_NULL) timed out");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Calls ShowWindow only if the target window is responsive.
    /// Returns true if ShowWindow was called and succeeded.
    /// </summary>
    public static bool SafeShowWindow(IntPtr hWnd, int nCmdShow)
    {
        if (!IsWindowResponsive(hWnd))
            return false;

        return User32.ShowWindow(hWnd, nCmdShow);
    }

    /// <summary>
    /// Calls SetWindowPos only if the target window is responsive.
    /// Returns true if SetWindowPos was called and succeeded.
    /// </summary>
    public static bool SafeSetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy,
        uint uFlags)
    {
        if (!IsWindowResponsive(hWnd))
            return false;

        return User32.SetWindowPos(hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);
    }

    /// <summary>
    /// Builds a description string for the window (hWnd, process, title) and
    /// logs + raises the HungWindowDetected event.
    /// </summary>
    private static void ReportHungWindow(IntPtr hWnd, string reason)
    {
        string title = GetWindowTitle(hWnd);
        string processName = "unknown";
        uint processId = 0;

        try
        {
            User32.GetWindowThreadProcessId(hWnd, out processId);
            if (processId != 0)
            {
                var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
        }
        catch
        {
            // Process may have exited
        }

        var description = $"hWnd=0x{hWnd:X}, Process={processName} (PID {processId}), Title=\"{title}\"";
        var logMessage = $"Hung window detected — {reason}: {description}";
        AppLogger.Instance.Warn(logMessage);

        HungWindowDetected?.Invoke(
            null,
            new HungWindowEventArgs(hWnd, processName, processId, title, reason));
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        try
        {
            int length = User32.GetWindowTextLength(hWnd);
            if (length == 0) return string.Empty;

            var buffer = new char[length + 1];
            User32.GetWindowText(hWnd, buffer, buffer.Length);
            return new string(buffer, 0, length);
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Event args raised when a hung or unresponsive window is detected.
/// </summary>
public class HungWindowEventArgs : EventArgs
{
    public IntPtr WindowHandle { get; }
    public string ProcessName { get; }
    public uint ProcessId { get; }
    public string WindowTitle { get; }
    public string Reason { get; }

    public HungWindowEventArgs(
        IntPtr windowHandle,
        string processName,
        uint processId,
        string windowTitle,
        string reason)
    {
        WindowHandle = windowHandle;
        ProcessName = processName;
        ProcessId = processId;
        WindowTitle = windowTitle;
        Reason = reason;
    }
}
