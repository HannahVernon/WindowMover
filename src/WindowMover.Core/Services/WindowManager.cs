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
                    ExecutablePath = exePath,
                    DisplayName = GetFriendlyAppName(new WindowInfo
                    {
                        Handle = hWnd,
                        Title = title,
                        ProcessName = processName,
                        ExecutablePath = exePath
                    })
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
    /// Captures the current window layout — which window is on which monitor right now.
    /// Returns a per-window list of WindowRules reflecting the actual state of the desktop.
    /// Each individual window gets its own rule, enabling per-window monitor assignments.
    /// </summary>
    public List<WindowRule> CaptureCurrentLayout(IReadOnlyList<MonitorInfo> monitors)
    {
        var windows = GetVisibleWindows();
        var rules = new List<WindowRule>();

        foreach (var window in windows)
        {
            var hMonitor = User32.MonitorFromWindow(window.Handle, User32.MONITOR_DEFAULTTONEAREST);
            var monitor = MatchMonitorByHandle(hMonitor, monitors);
            if (monitor == null) continue;

            rules.Add(new WindowRule
            {
                ProcessName = window.ProcessName,
                DisplayName = window.DisplayName ?? GetFriendlyAppName(window),
                ExecutablePath = window.ExecutablePath,
                TargetMonitorId = monitor.DeviceId,
                WindowTitle = window.Title
            });
        }

        return rules;
    }

    /// <summary>
    /// Matches an HMONITOR handle to a MonitorInfo by comparing screen bounds.
    /// </summary>
    private static MonitorInfo? MatchMonitorByHandle(IntPtr hMonitor, IReadOnlyList<MonitorInfo> monitors)
    {
        var info = new User32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<User32.MONITORINFO>() };
        if (!User32.GetMonitorInfo(hMonitor, ref info))
            return null;

        var rect = info.rcMonitor;
        return monitors.FirstOrDefault(m =>
            m.Bounds.X == rect.Left &&
            m.Bounds.Y == rect.Top &&
            m.Bounds.Width == rect.Right - rect.Left &&
            m.Bounds.Height == rect.Bottom - rect.Top);
    }

    /// <summary>
    /// Applies window rules: moves each matching window to its target monitor.
    /// Per-window rules (WindowTitle set) are applied first; remaining windows
    /// fall through to per-process rules (WindowTitle null/empty).
    /// Z-order is set based on rule list order: first rule per monitor = topmost.
    /// </summary>
    public void ApplyRules(IReadOnlyList<WindowRule> rules, IReadOnlyList<MonitorInfo> monitors)
    {
        var windows = GetVisibleWindows();
        var managedHandles = new HashSet<IntPtr>();

        // Separate per-window and per-process rules
        var perWindowRules = rules.Where(r => !string.IsNullOrEmpty(r.WindowTitle)).ToList();
        var perProcessRules = rules.Where(r => string.IsNullOrEmpty(r.WindowTitle)).ToList();

        // Phase 1: Apply per-window rules (match by ProcessName + WindowTitle)
        foreach (var rule in perWindowRules)
        {
            var targetMonitor = monitors.FirstOrDefault(m => m.DeviceId == rule.TargetMonitorId);
            if (targetMonitor == null) continue;

            var match = windows.FirstOrDefault(w =>
                !managedHandles.Contains(w.Handle) &&
                w.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                w.Title.Equals(rule.WindowTitle, StringComparison.OrdinalIgnoreCase));

            // Fuzzy fallback: match by title suffix (e.g., "— Mozilla Firefox")
            match ??= windows.FirstOrDefault(w =>
                !managedHandles.Contains(w.Handle) &&
                w.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                TitleSuffixMatch(w.Title, rule.WindowTitle!));

            if (match != null)
            {
                MoveWindowToMonitor(match.Handle, targetMonitor);
                managedHandles.Add(match.Handle);
            }
        }

        // Phase 2: Apply per-process rules (legacy, no WindowTitle)
        foreach (var rule in perProcessRules)
        {
            var targetMonitor = monitors.FirstOrDefault(m => m.DeviceId == rule.TargetMonitorId);
            if (targetMonitor == null) continue;

            var matchingWindows = windows
                .Where(w => !managedHandles.Contains(w.Handle) &&
                            w.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase));

            foreach (var window in matchingWindows)
            {
                MoveWindowToMonitor(window.Handle, targetMonitor);
                managedHandles.Add(window.Handle);
            }
        }

        // Apply z-order from rule list order (first rule = topmost).
        // Iterate rules in reverse so the first rule's windows end up on top last.
        var zOrderFlags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            IEnumerable<WindowInfo> ruleWindows;
            if (!string.IsNullOrEmpty(rules[i].WindowTitle))
            {
                // Per-window rule: find specific window
                var match = windows.FirstOrDefault(w =>
                    w.ProcessName.Equals(rules[i].ProcessName, StringComparison.OrdinalIgnoreCase) &&
                    (w.Title.Equals(rules[i].WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                     TitleSuffixMatch(w.Title, rules[i].WindowTitle!)));
                ruleWindows = match != null ? [match] : [];
            }
            else
            {
                // Per-process rule: all windows
                ruleWindows = windows.Where(w =>
                    w.ProcessName.Equals(rules[i].ProcessName, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var window in ruleWindows)
            {
                User32.SetWindowPos(window.Handle, User32.HWND_TOPMOST,
                    0, 0, 0, 0, zOrderFlags);
                User32.SetWindowPos(window.Handle, User32.HWND_NOTOPMOST,
                    0, 0, 0, 0, zOrderFlags);
            }
        }
    }

    /// <summary>
    /// Checks if two window titles share the same suffix (app name portion).
    /// E.g., "Reddit — Mozilla Firefox" and "YouTube — Mozilla Firefox" both
    /// have suffix "Mozilla Firefox".
    /// </summary>
    private static bool TitleSuffixMatch(string title1, string title2)
    {
        var delimiters = new[] { " — ", " - ", " | " };
        foreach (var delim in delimiters)
        {
            var idx1 = title1.LastIndexOf(delim, StringComparison.Ordinal);
            var idx2 = title2.LastIndexOf(delim, StringComparison.Ordinal);
            if (idx1 > 0 && idx2 > 0)
            {
                var suffix1 = title1[(idx1 + delim.Length)..];
                var suffix2 = title2[(idx2 + delim.Length)..];
                if (suffix1.Equals(suffix2, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Moves a window to the center of the specified monitor's work area.
    /// Handles maximized and minimized windows by restoring first, then moving,
    /// then re-applying the original state.
    /// </summary>
    public static void MoveWindowToMonitor(IntPtr hWnd, MonitorInfo targetMonitor)
    {
        var placement = User32.WINDOWPLACEMENT.Default;
        User32.GetWindowPlacement(hWnd, ref placement);

        bool wasMaximized = placement.showCmd == User32.SW_SHOWMAXIMIZED;
        bool wasMinimized = placement.showCmd == User32.SW_SHOWMINIMIZED;

        if (wasMaximized || wasMinimized)
        {
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
            User32.ShowWindow(hWnd, User32.SW_MAXIMIZE);
        }
        else if (wasMinimized)
        {
            User32.ShowWindow(hWnd, User32.SW_MINIMIZE);
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
        // For UWP apps (resolved from ApplicationFrameHost), prefer the window title
        // since the exe's FileDescription is often unhelpful (e.g. "Application Frame Host")
        if (IsUwpProcess(window.ProcessName))
        {
            if (!string.IsNullOrWhiteSpace(window.Title) &&
                window.Title.Length < 60 &&
                !window.Title.Contains('\\') &&
                !window.Title.Contains('/'))
            {
                return window.Title;
            }
        }

        // Try to get the FileDescription from the process executable
        try
        {
            if (window.ExecutablePath != null)
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(window.ExecutablePath);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription) &&
                    !versionInfo.FileDescription.Contains("Application Frame Host", StringComparison.OrdinalIgnoreCase))
                    return versionInfo.FileDescription;
            }
        }
        catch { }

        // For any app, the window title is a decent fallback
        if (!string.IsNullOrWhiteSpace(window.Title) &&
            window.Title.Length < 60 &&
            !window.Title.Contains('\\') &&
            !window.Title.Contains('/'))
        {
            return window.Title;
        }

        return window.ProcessName;
    }

    private static bool IsUwpProcess(string processName)
    {
        // Common UWP host processes whose FileDescription is not user-friendly
        var uwpHosts = new[] {
            "ApplicationFrameHost", "SystemSettings", "WinStore.App",
            "Microsoft.Photos", "WindowsCalculator", "ScreenSketch",
            "Video.UI", "Music.UI", "HxOutlook", "HxCalendarAppImm"
        };
        return uwpHosts.Any(h => processName.Contains(h, StringComparison.OrdinalIgnoreCase));
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
    public string? DisplayName { get; set; }
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
    public uint ProcessId { get; set; }
}
