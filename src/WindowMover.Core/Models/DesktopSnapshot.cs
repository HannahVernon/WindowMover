namespace WindowMover.Core.Models;

/// <summary>
/// A complete snapshot of all visible windows on the desktop.
/// Periodically saved to disk for cross-reboot restoration.
/// </summary>
public class DesktopSnapshot
{
    /// <summary>
    /// When this snapshot was captured (UTC).
    /// </summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Fingerprint of the monitor setup at capture time.
    /// Used to ensure we only restore when the same monitors are connected.
    /// </summary>
    public string SetupFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// The machine name, to avoid cross-machine snapshot collisions.
    /// </summary>
    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>
    /// All tracked window states at the time of capture.
    /// </summary>
    public List<WindowSnapshot> Windows { get; set; } = [];
}
