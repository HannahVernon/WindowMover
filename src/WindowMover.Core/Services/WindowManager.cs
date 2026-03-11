using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowMover.Core.Models;
using WindowMover.Core.Native;

namespace WindowMover.Core.Services;

/// <summary>
/// Enumerates visible application windows and moves them to target monitors
/// based on window rules.
/// </summary>
public class WindowManager
{
    /// <summary>
    /// Gets all visible top-level application windows with their process info.
    /// </summary>
    public List<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();

        User32.EnumWindows((hWnd, _) =>
        {
            if (!IsAppWindow(hWnd))
                return true;

            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            User32.GetWindowThreadProcessId(hWnd, out var processId);
            try
            {
                var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName;
                var exePath = GetProcessPath(process);

                // UWP apps are hosted by ApplicationFrameHost — resolve the real app
                if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = ResolveUwpApp(hWnd);
                    if (resolved != null)
                    {
                        processName = resolved.Value.ProcessName;
                        exePath = resolved.Value.ExePath;
                    }
                    else
                    {
                        // Use the window title as a fallback display hint
                        processName = SanitizeWindowTitleAsName(title);
                    }
                }

                windows.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessName = processName,
                    ProcessId = processId,
                    ExecutablePath = exePath
                });
            }
            catch (ArgumentException)
            {
                // Process already exited
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// Gets a deduplicated list of process names from currently visible windows.
    /// </summary>
    public List<AppInfo> GetRunningApps()
    {
        var windows = GetVisibleWindows();
        return windows
            .GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppInfo
            {
                ProcessName = g.Key,
                DisplayName = GetFriendlyAppName(g.First()),
                ExecutablePath = g.First().ExecutablePath,
                WindowCount = g.Count()
            })
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Applies window rules: moves each matching app's windows to its target monitor.
    /// </summary>
    public void ApplyRules(IReadOnlyList<WindowRule> rules, IReadOnlyList<MonitorInfo> monitors)
    {
        var windows = GetVisibleWindows();

        foreach (var rule in rules)
        {
            var targetMonitor = monitors.FirstOrDefault(m => m.DeviceId == rule.TargetMonitorId);
            if (targetMonitor == null) continue;

            var matchingWindows = windows
                .Where(w => w.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase));

            foreach (var window in matchingWindows)
            {
                MoveWindowToMonitor(window.Handle, targetMonitor);
            }
        }
    }

    /// <summary>
    /// Moves a window to the center of the specified monitor's work area.
    /// Handles maximized windows by restoring first, then moving, then re-maximizing.
    /// </summary>
    public static void MoveWindowToMonitor(IntPtr hWnd, MonitorInfo targetMonitor)
    {
        var placement = User32.WINDOWPLACEMENT.Default;
        User32.GetWindowPlacement(hWnd, ref placement);

        bool wasMaximized = placement.showCmd == User32.SW_SHOWMAXIMIZED;

        if (wasMaximized)
        {
            // Restore the window first so we can move it
            User32.ShowWindow(hWnd, User32.SW_RESTORE);
        }

        // Get current window size
        User32.GetWindowRect(hWnd, out var currentRect);
        int windowWidth = currentRect.Width;
        int windowHeight = currentRect.Height;

        var workArea = targetMonitor.WorkArea;

        // Clamp window size to target work area
        windowWidth = Math.Min(windowWidth, workArea.Width);
        windowHeight = Math.Min(windowHeight, workArea.Height);

        // Center the window in the target monitor's work area
        int x = workArea.X + (workArea.Width - windowWidth) / 2;
        int y = workArea.Y + (workArea.Height - windowHeight) / 2;

        User32.SetWindowPos(hWnd, IntPtr.Zero, x, y, windowWidth, windowHeight,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

        if (wasMaximized)
        {
            // Re-maximize on the new monitor
            User32.ShowWindow(hWnd, User32.SW_MAXIMIZE);
        }
    }

    private static bool IsAppWindow(IntPtr hWnd)
    {
        if (!User32.IsWindowVisible(hWnd))
            return false;

        // Must have no owner (top-level)
        if (User32.GetParent(hWnd) != IntPtr.Zero)
            return false;

        nint exStyle = User32.GetWindowLongPtr(hWnd, User32.GWL_EXSTYLE);
        nint style = User32.GetWindowLongPtr(hWnd, User32.GWL_STYLE);

        // Skip tool windows (unless they also have APPWINDOW)
        if ((exStyle & User32.WS_EX_TOOLWINDOW) != 0 && (exStyle & User32.WS_EX_APPWINDOW) == 0)
            return false;

        // Skip windows without caption (system overlays, etc.)
        if ((style & User32.WS_CAPTION) != User32.WS_CAPTION)
            return false;

        // Skip NOACTIVATE windows
        if ((exStyle & User32.WS_EX_NOACTIVATE) != 0)
            return false;

        // Skip windows owned by other windows
        if (User32.GetWindow(hWnd, User32.GW_OWNER) != IntPtr.Zero)
            return false;

        // Skip specific system classes
        var className = GetClassName(hWnd);
        if (className is "Progman" or "Shell_TrayWnd" or "WorkerW" or "Shell_SecondaryTrayWnd")
            return false;

        return true;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        int length = User32.GetWindowTextLength(hWnd);
        if (length == 0) return string.Empty;

        var buffer = new char[length + 1];
        User32.GetWindowText(hWnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var buffer = new char[256];
        int length = User32.GetClassName(hWnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    private static string? GetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            // Access denied for system/elevated processes
            return null;
        }
    }

    private static string GetFriendlyAppName(WindowInfo window)
    {
        // Try to get the FileDescription from the process executable
        try
        {
            if (window.ExecutablePath != null)
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(window.ExecutablePath);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                    return versionInfo.FileDescription;
            }
        }
        catch { }

        // For UWP apps, the window title is often a good display name
        if (!string.IsNullOrWhiteSpace(window.Title) &&
            window.Title.Length < 60 &&
            !window.Title.Contains('\\') &&
            !window.Title.Contains('/'))
        {
            return window.Title;
        }

        // Fallback to process name
        return window.ProcessName;
    }

    /// <summary>
    /// Resolves the real UWP app process behind an ApplicationFrameHost window
    /// by enumerating child windows to find one owned by a different process.
    /// </summary>
    private static (string ProcessName, string? ExePath)? ResolveUwpApp(IntPtr frameHwnd)
    {
        User32.GetWindowThreadProcessId(frameHwnd, out var frameProcessId);

        (string ProcessName, string? ExePath)? result = null;

        User32.EnumChildWindows(frameHwnd, (childHwnd, _) =>
        {
            User32.GetWindowThreadProcessId(childHwnd, out var childProcessId);
            if (childProcessId != 0 && childProcessId != frameProcessId)
            {
                try
                {
                    var childProcess = Process.GetProcessById((int)childProcessId);
                    if (!string.IsNullOrEmpty(childProcess.ProcessName))
                    {
                        result = (childProcess.ProcessName, GetProcessPath(childProcess));
                        return false; // stop enumerating
                    }
                }
                catch { }
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Extracts a usable app name from a window title (for UWP fallback).
    /// </summary>
    private static string SanitizeWindowTitleAsName(string title)
    {
        // Trim common suffixes like " - Microsoft Edge", take the app part
        var dashIdx = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0)
        {
            var suffix = title[(dashIdx + 3)..].Trim();
            if (suffix.Length > 2)
                return suffix;
        }
        return title.Length > 40 ? title[..40] : title;
    }
}

/// <summary>
/// Information about a single visible window.
/// </summary>
public class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public uint ProcessId { get; set; }
    public string? ExecutablePath { get; set; }
}

/// <summary>
/// Information about a running application (may have multiple windows).
/// </summary>
public class AppInfo
{
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public int WindowCount { get; set; }
}
