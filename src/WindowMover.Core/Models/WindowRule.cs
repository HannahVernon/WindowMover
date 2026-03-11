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

    public override string ToString() => $"{DisplayName} → {TargetMonitorId}";
}
