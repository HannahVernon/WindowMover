using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowMover.Core.Models;
using WindowMover.Core.Native;

namespace WindowMover.Core.Services;

/// <summary>
/// Tracks individual windows across the session using SetProp/GetProp UIDs,
/// periodically captures desktop snapshots, and restores window positions
/// from the last snapshot on startup.
/// </summary>
public class WindowTracker : IDisposable
{
    private const string PropName = "WindowMover_UID";
    private const string SnapshotFileName = "last-snapshot.json";
    private const string ConfigFileName = "config.json";
    private const int MaxTitleHistory = 5;

    private readonly string _snapshotDir;
    private readonly string _configDir;
    private readonly WindowManager _windowManager;
    private readonly MonitorIdentifier _monitorIdentifier;
    private readonly JsonSerializerOptions _jsonOptions;

    private long _nextUid;
    private readonly Dictionary<long, List<string>> _titleHistory = new();
    private System.Threading.Timer? _snapshotTimer;
    private bool _disposed;

    public WindowTracker(WindowManager windowManager, MonitorIdentifier monitorIdentifier)
        : this(windowManager, monitorIdentifier, GetDefaultSnapshotDir())
    {
    }

    public WindowTracker(WindowManager windowManager, MonitorIdentifier monitorIdentifier, string snapshotDir)
    {
        _windowManager = windowManager;
        _monitorIdentifier = monitorIdentifier;
        _snapshotDir = snapshotDir;
        _configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowMover");
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
        Directory.CreateDirectory(_snapshotDir);
        Directory.CreateDirectory(_configDir);
        _nextUid = LoadNextUid();
    }

    /// <summary>
    /// Starts the periodic snapshot timer.
    /// </summary>
    /// <param name="intervalSeconds">Seconds between snapshots (default: 30).</param>
    public void StartPeriodicSnapshots(int intervalSeconds = 30)
    {
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        _snapshotTimer = new System.Threading.Timer(
            _ => CaptureAndSaveSnapshot(),
            null,
            interval,  // initial delay: one interval
            interval); // recurring interval
        AppLogger.Instance.Info($"Periodic snapshots started (every {intervalSeconds}s)");
    }

    /// <summary>
    /// Stops the periodic snapshot timer.
    /// </summary>
    public void StopPeriodicSnapshots()
    {
        _snapshotTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _snapshotTimer?.Dispose();
        _snapshotTimer = null;
    }

    /// <summary>
    /// Tags all currently visible windows with UIDs (if not already tagged)
    /// and captures a snapshot of their current state.
    /// </summary>
    public DesktopSnapshot CaptureSnapshot()
    {
        var monitors = _monitorIdentifier.GetConnectedMonitors();
        var isRemote = SessionDetector.IsRemoteSession();
        var fingerprint = MonitorSetup.ComputeFingerprint(monitors, isRemote);

        var windows = _windowManager.GetVisibleWindows();
        var snapshot = new DesktopSnapshot
        {
            CapturedAt = DateTime.UtcNow,
            SetupFingerprint = fingerprint
        };

        int zOrder = 0;
        foreach (var window in windows)
        {
            var uid = EnsureUid(window.Handle);
            var monitorDeviceId = ResolveMonitorDeviceId(window.Handle, monitors);
            var placement = GetWindowPlacement(window.Handle);
            var title = GetWindowTitle(window.Handle);

            // Track title history for this UID
            UpdateTitleHistory(uid, title);

            snapshot.Windows.Add(new WindowSnapshot
            {
                Uid = uid,
                ProcessName = window.ProcessName,
                Title = title,
                TitleHistory = GetTitleHistory(uid),
                MonitorDeviceId = monitorDeviceId,
                Bounds = GetNormalBounds(placement),
                ZOrder = zOrder++,
                ShowState = GetShowState(placement),
                ExecutablePath = window.ExecutablePath
            });
        }

        return snapshot;
    }

    /// <summary>
    /// Saves the given snapshot to disk.
    /// </summary>
    public void SaveSnapshot(DesktopSnapshot snapshot)
    {
        try
        {
            var path = GetSnapshotPath();
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error("Failed to save desktop snapshot", ex);
        }
    }

    /// <summary>
    /// Captures and saves a snapshot in one step (called by the timer).
    /// </summary>
    public void CaptureAndSaveSnapshot()
    {
        try
        {
            var snapshot = CaptureSnapshot();
            SaveSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error("Periodic snapshot capture failed", ex);
        }
    }

    /// <summary>
    /// Loads the last saved snapshot from disk.
    /// </summary>
    public DesktopSnapshot? LoadLastSnapshot()
    {
        try
        {
            var path = GetSnapshotPath();
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DesktopSnapshot>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error("Failed to load desktop snapshot", ex);
            return null;
        }
    }

    /// <summary>
    /// Attempts to restore window positions from the last saved snapshot.
    /// Matches current windows to snapshot entries by ProcessName + Title similarity.
    /// Returns the number of windows successfully restored.
    /// </summary>
    public int RestoreFromSnapshot(DesktopSnapshot snapshot, IReadOnlyList<MonitorInfo> currentMonitors)
    {
        var windows = _windowManager.GetVisibleWindows();
        var snapshotEntries = new List<WindowSnapshot>(snapshot.Windows);
        int restored = 0;

        // Sort snapshot entries by z-order (lowest z-order = topmost) so we restore
        // in reverse order and the topmost window ends up on top
        snapshotEntries.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));

        // Phase 1: Match by ProcessName + exact title
        var matched = new Dictionary<IntPtr, WindowSnapshot>();
        var usedSnapshots = new HashSet<int>(); // track by snapshot index

        for (int si = 0; si < snapshotEntries.Count; si++)
        {
            var snap = snapshotEntries[si];
            var candidate = windows.FirstOrDefault(w =>
                !matched.ContainsKey(w.Handle) &&
                w.ProcessName.Equals(snap.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                w.Title.Equals(snap.Title, StringComparison.OrdinalIgnoreCase));

            if (candidate != null)
            {
                matched[candidate.Handle] = snap;
                usedSnapshots.Add(si);
            }
        }

        // Phase 2: Match by ProcessName + title history (title may have changed since snapshot)
        for (int si = 0; si < snapshotEntries.Count; si++)
        {
            if (usedSnapshots.Contains(si)) continue;
            var snap = snapshotEntries[si];

            var candidate = windows.FirstOrDefault(w =>
                !matched.ContainsKey(w.Handle) &&
                w.ProcessName.Equals(snap.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                snap.TitleHistory.Any(t => t.Equals(w.Title, StringComparison.OrdinalIgnoreCase)));

            if (candidate != null)
            {
                matched[candidate.Handle] = snap;
                usedSnapshots.Add(si);
            }
        }

        // Phase 3: Match by ProcessName + title substring (fuzzy)
        for (int si = 0; si < snapshotEntries.Count; si++)
        {
            if (usedSnapshots.Contains(si)) continue;
            var snap = snapshotEntries[si];

            var candidate = windows.FirstOrDefault(w =>
                !matched.ContainsKey(w.Handle) &&
                w.ProcessName.Equals(snap.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                TitleFuzzyMatch(w.Title, snap.Title));

            if (candidate != null)
            {
                matched[candidate.Handle] = snap;
                usedSnapshots.Add(si);
            }
        }

        // Phase 4: Match remaining by ProcessName only + position proximity
        for (int si = 0; si < snapshotEntries.Count; si++)
        {
            if (usedSnapshots.Contains(si)) continue;
            var snap = snapshotEntries[si];

            var candidate = windows
                .Where(w =>
                    !matched.ContainsKey(w.Handle) &&
                    w.ProcessName.Equals(snap.ProcessName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(w => PositionDistance(w.Handle, snap.Bounds))
                .FirstOrDefault();

            if (candidate != null)
            {
                matched[candidate.Handle] = snap;
                usedSnapshots.Add(si);
            }
        }

        // Apply: move matched windows to their saved positions
        // Process in reverse z-order (bottom first) so topmost ends up on top
        var orderedMatches = matched
            .OrderByDescending(kv => kv.Value.ZOrder)
            .ToList();

        foreach (var (hwnd, snap) in orderedMatches)
        {
            var targetMonitor = currentMonitors.FirstOrDefault(m => m.DeviceId == snap.MonitorDeviceId);
            if (targetMonitor == null) continue;

            if (RestoreWindow(hwnd, snap, targetMonitor))
            {
                restored++;
                // Tag the restored window with a fresh UID
                EnsureUid(hwnd);
            }
        }

        AppLogger.Instance.Info($"Restored {restored}/{matched.Count} matched windows from snapshot " +
                                $"({snapshot.Windows.Count} in snapshot, {windows.Count} currently visible)");
        return restored;
    }

    /// <summary>
    /// Gets the UID for a window, or 0 if not tagged.
    /// </summary>
    public static long GetUid(IntPtr hwnd)
    {
        var prop = User32.GetProp(hwnd, PropName);
        return prop == IntPtr.Zero ? 0L : prop.ToInt64();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Final snapshot and config save before shutdown
        try
        {
            CaptureAndSaveSnapshot();
            SaveNextUid();
        }
        catch { }

        StopPeriodicSnapshots();
        GC.SuppressFinalize(this);
    }

    #region Private helpers

    /// <summary>
    /// Ensures the window has a UID. Returns the existing or newly assigned UID.
    /// </summary>
    private long EnsureUid(IntPtr hwnd)
    {
        var existing = User32.GetProp(hwnd, PropName);
        if (existing != IntPtr.Zero)
            return existing.ToInt64();

        var uid = _nextUid++;
        User32.SetProp(hwnd, PropName, (IntPtr)uid);
        SaveNextUid();
        return uid;
    }

    private string ResolveMonitorDeviceId(IntPtr hwnd, IReadOnlyList<MonitorInfo> monitors)
    {
        var hMonitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
        var info = new User32.MONITORINFO { cbSize = Marshal.SizeOf<User32.MONITORINFO>() };
        if (!User32.GetMonitorInfo(hMonitor, ref info))
            return string.Empty;

        var rect = info.rcMonitor;
        var monitor = monitors.FirstOrDefault(m =>
            m.Bounds.X == rect.Left &&
            m.Bounds.Y == rect.Top &&
            m.Bounds.Width == rect.Right - rect.Left &&
            m.Bounds.Height == rect.Bottom - rect.Top);

        return monitor?.DeviceId ?? string.Empty;
    }

    private static User32.WINDOWPLACEMENT GetWindowPlacement(IntPtr hwnd)
    {
        var placement = User32.WINDOWPLACEMENT.Default;
        User32.GetWindowPlacement(hwnd, ref placement);
        return placement;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = User32.GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;
        var buffer = new char[length + 1];
        User32.GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    private static SnapshotRect GetNormalBounds(User32.WINDOWPLACEMENT placement)
    {
        var r = placement.rcNormalPosition;
        return new SnapshotRect
        {
            X = r.Left,
            Y = r.Top,
            Width = r.Right - r.Left,
            Height = r.Bottom - r.Top
        };
    }

    private static WindowShowState GetShowState(User32.WINDOWPLACEMENT placement) =>
        placement.showCmd switch
        {
            User32.SW_SHOWMAXIMIZED => WindowShowState.Maximized,
            User32.SW_SHOWMINIMIZED => WindowShowState.Minimized,
            _ => WindowShowState.Normal
        };

    private void UpdateTitleHistory(long uid, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        if (!_titleHistory.TryGetValue(uid, out var history))
        {
            history = [];
            _titleHistory[uid] = history;
        }

        // Don't add duplicate of the most recent title
        if (history.Count > 0 && history[^1].Equals(title, StringComparison.OrdinalIgnoreCase))
            return;

        history.Add(title);

        // Keep only the last N titles
        if (history.Count > MaxTitleHistory)
            history.RemoveAt(0);
    }

    private List<string> GetTitleHistory(long uid)
    {
        return _titleHistory.TryGetValue(uid, out var history)
            ? new List<string>(history)
            : [];
    }

    /// <summary>
    /// Fuzzy title match: checks if titles share a significant common substring.
    /// Handles browser tabs where the URL/domain is the stable part.
    /// </summary>
    private static bool TitleFuzzyMatch(string currentTitle, string snapshotTitle)
    {
        if (string.IsNullOrEmpty(currentTitle) || string.IsNullOrEmpty(snapshotTitle))
            return false;

        // Check if one contains the other (handles "Reddit — Mozilla Firefox" vs "YouTube — Mozilla Firefox")
        // The app suffix (e.g., "— Mozilla Firefox") is the stable part
        var currentParts = currentTitle.Split(new[] { " — ", " - ", " | " }, StringSplitOptions.TrimEntries);
        var snapshotParts = snapshotTitle.Split(new[] { " — ", " - ", " | " }, StringSplitOptions.TrimEntries);

        // If both titles have the same suffix (app name), consider it a fuzzy match
        if (currentParts.Length > 1 && snapshotParts.Length > 1)
        {
            var currentSuffix = currentParts[^1];
            var snapshotSuffix = snapshotParts[^1];
            if (currentSuffix.Equals(snapshotSuffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Calculates the distance between a window's current position and a snapshot position.
    /// Used as a tiebreaker when multiple windows have the same process name.
    /// </summary>
    private static double PositionDistance(IntPtr hwnd, SnapshotRect snapBounds)
    {
        User32.GetWindowRect(hwnd, out var rect);
        double dx = rect.Left - snapBounds.X;
        double dy = rect.Top - snapBounds.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Restores a single window to its snapshot position and state.
    /// </summary>
    private static bool RestoreWindow(IntPtr hwnd, WindowSnapshot snap, MonitorInfo targetMonitor)
    {
        try
        {
            var placement = User32.WINDOWPLACEMENT.Default;
            User32.GetWindowPlacement(hwnd, ref placement);

            bool wasMaximized = placement.showCmd == User32.SW_SHOWMAXIMIZED;
            bool wasMinimized = placement.showCmd == User32.SW_SHOWMINIMIZED;

            // Restore to normal state first to allow repositioning
            if (wasMaximized || wasMinimized)
            {
                User32.ShowWindow(hwnd, User32.SW_RESTORE);
            }

            // Move to saved position
            User32.SetWindowPos(hwnd, IntPtr.Zero,
                snap.Bounds.X, snap.Bounds.Y,
                snap.Bounds.Width, snap.Bounds.Height,
                User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

            // Restore original show state
            switch (snap.ShowState)
            {
                case WindowShowState.Maximized:
                    User32.ShowWindow(hwnd, User32.SW_MAXIMIZE);
                    break;
                case WindowShowState.Minimized:
                    User32.ShowWindow(hwnd, User32.SW_MINIMIZE);
                    break;
            }

            // Apply z-order using the TOPMOST/NOTOPMOST trick
            var zFlags = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE;
            User32.SetWindowPos(hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0, zFlags);
            User32.SetWindowPos(hwnd, User32.HWND_NOTOPMOST, 0, 0, 0, 0, zFlags);

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error($"Failed to restore window for {snap.ProcessName}: {snap.Title}", ex);
            return false;
        }
    }

    private string GetSnapshotPath() =>
        Path.Combine(_snapshotDir, SnapshotFileName);

    private string GetConfigPath() =>
        Path.Combine(_configDir, ConfigFileName);

    /// <summary>
    /// Loads the next UID value from config.json, or starts at 1 if no config exists.
    /// </summary>
    private long LoadNextUid()
    {
        try
        {
            var path = GetConfigPath();
            if (!File.Exists(path)) return 1;

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions);
            return config?.NextWindowUid ?? 1;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error("Failed to load config — starting UID from 1", ex);
            return 1;
        }
    }

    /// <summary>
    /// Persists the current next UID value to config.json.
    /// </summary>
    private void SaveNextUid()
    {
        try
        {
            var path = GetConfigPath();
            AppConfig config;

            // Preserve other config values if the file already exists
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<AppConfig>(existing, _jsonOptions) ?? new AppConfig();
            }
            else
            {
                config = new AppConfig();
            }

            config.NextWindowUid = _nextUid;
            var json = JsonSerializer.Serialize(config, _jsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error("Failed to save config", ex);
        }
    }

    private static string GetDefaultSnapshotDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowMover", "snapshots");

    #endregion
}

/// <summary>
/// Persistent application configuration stored in %APPDATA%\WindowMover\config.json.
/// </summary>
internal class AppConfig
{
    /// <summary>
    /// The next window UID to assign. Monotonically increasing across app restarts.
    /// </summary>
    public long NextWindowUid { get; set; } = 1;
}
