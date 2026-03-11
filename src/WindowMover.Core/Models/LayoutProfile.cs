namespace WindowMover.Core.Models;

/// <summary>
/// A complete layout profile: a set of window rules tied to a specific monitor setup.
/// </summary>
public class LayoutProfile
{
    /// <summary>
    /// The fingerprint of the monitor setup this profile applies to.
    /// </summary>
    public string SetupFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// User-assigned name for this profile (defaults to the setup name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this profile was created during an RDP session.
    /// </summary>
    public bool IsRemoteSession { get; set; }

    /// <summary>
    /// Snapshot of the monitor setup for reference (manufacturer/model info).
    /// </summary>
    public MonitorSetup Setup { get; set; } = new();

    /// <summary>
    /// The window placement rules for this profile.
    /// </summary>
    public List<WindowRule> Rules { get; set; } = [];

    /// <summary>
    /// When this profile was last modified.
    /// </summary>
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}
