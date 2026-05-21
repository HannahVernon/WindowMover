namespace WindowMover.Core.Models;

/// <summary>
/// A rule that maps an application (by process name) to a target monitor.
/// </summary>
public class WindowRule
{
    /// <summary>
    /// The process name to match (e.g., "chrome", "OUTLOOK", "devenv").
    /// Case-insensitive matching is used.
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Optional executable path for disambiguation when multiple processes
    /// share the same name.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// The DeviceId of the target monitor where this app's windows should be placed.
    /// </summary>
    public string TargetMonitorId { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly display name for the application (e.g., "Google Chrome").
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Optional Process ID for distinguishing multiple windows of the same process.
    /// Used at runtime; treated as a hint when loading saved rules.
    /// </summary>
    public uint ProcessId { get; set; }

    /// <summary>
    /// The window title for per-window rules. When set, this rule applies only to
    /// the specific window with this title. When null/empty, the rule applies to
    /// all windows of the process (backward-compatible behavior).
    /// </summary>
    public string? WindowTitle { get; set; }

    /// <summary>
    /// Proportional X position within the monitor's work area (0.0 = left edge, 1.0 = right edge).
    /// Null when no position has been captured (legacy rules center the window).
    /// </summary>
    public double? RelativeX { get; set; }

    /// <summary>
    /// Proportional Y position within the monitor's work area (0.0 = top edge, 1.0 = bottom edge).
    /// </summary>
    public double? RelativeY { get; set; }

    /// <summary>
    /// Proportional width relative to the monitor's work area (0.0 to 1.0).
    /// </summary>
    public double? RelativeWidth { get; set; }

    /// <summary>
    /// Proportional height relative to the monitor's work area (0.0 to 1.0).
    /// </summary>
    public double? RelativeHeight { get; set; }

    /// <summary>
    /// Window show state at capture time: Normal, Maximized, or Minimized.
    /// </summary>
    public WindowShowState? ShowState { get; set; }

    /// <summary>
    /// Work area width (pixels) at the time of capture. Used for diagnostics
    /// and to detect resolution changes.
    /// </summary>
    public int? CapturedWorkAreaWidth { get; set; }

    /// <summary>
    /// Work area height (pixels) at the time of capture.
    /// </summary>
    public int? CapturedWorkAreaHeight { get; set; }

    public override string ToString() => $"{DisplayName} → {TargetMonitorId}";
}
