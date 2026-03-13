namespace WindowMover.Core.Models;

/// <summary>
/// Captures the complete state of a single window at a point in time.
/// Used for periodic desktop snapshots and cross-reboot restoration.
/// </summary>
public class WindowSnapshot
{
    /// <summary>
    /// Unique identifier assigned via SetProp during the current session.
    /// Monotonically increasing Int64, persisted in config.json across app restarts.
    /// </summary>
    public long Uid { get; set; }

    /// <summary>
    /// Process name (e.g., "firefox", "devenv").
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Window title at the time of capture.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Previous window titles observed for this UID during this session.
    /// Helps with fuzzy matching after reboot when the active tab may have changed.
    /// </summary>
    public List<string> TitleHistory { get; set; } = [];

    /// <summary>
    /// DeviceId of the monitor this window was on.
    /// </summary>
    public string MonitorDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Window position and size (normal/restored bounds).
    /// </summary>
    public SnapshotRect Bounds { get; set; } = new();

    /// <summary>
    /// Z-order index (0 = topmost visible window).
    /// </summary>
    public int ZOrder { get; set; }

    /// <summary>
    /// Window show state: Normal, Maximized, or Minimized.
    /// </summary>
    public WindowShowState ShowState { get; set; } = WindowShowState.Normal;

    /// <summary>
    /// Optional executable path for additional matching confidence.
    /// </summary>
    public string? ExecutablePath { get; set; }
}

/// <summary>
/// Serializable rectangle (System.Drawing.Rectangle is not JSON-friendly).
/// </summary>
public class SnapshotRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public enum WindowShowState
{
    Normal,
    Maximized,
    Minimized
}
