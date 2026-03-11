using System.Security.Cryptography;
using System.Text;

namespace WindowMover.Core.Models;

/// <summary>
/// Represents a specific combination of connected monitors.
/// The fingerprint uniquely identifies the setup (e.g., "home dock", "work dock").
/// </summary>
public class MonitorSetup
{
    /// <summary>
    /// SHA256-based fingerprint of the sorted monitor device IDs.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// User-assigned name for this setup (e.g., "Home Office", "Work Desk").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this setup was detected during an RDP session.
    /// </summary>
    public bool IsRemoteSession { get; set; }

    /// <summary>
    /// The monitors in this setup (for display purposes in UI).
    /// </summary>
    public List<MonitorInfo> Monitors { get; set; } = [];

    /// <summary>
    /// Computes a stable fingerprint from a set of monitors.
    /// </summary>
    public static string ComputeFingerprint(IEnumerable<MonitorInfo> monitors, bool isRemote)
    {
        var ids = monitors
            .Select(m => m.DeviceId)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var prefix = isRemote ? "RDP:" : "LOCAL:";
        var combined = prefix + string.Join("|", ids);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// Creates a MonitorSetup from the currently connected monitors.
    /// </summary>
    public static MonitorSetup FromMonitors(IReadOnlyList<MonitorInfo> monitors, bool isRemote)
    {
        return new MonitorSetup
        {
            Fingerprint = ComputeFingerprint(monitors, isRemote),
            IsRemoteSession = isRemote,
            Monitors = [.. monitors],
            Name = GenerateDefaultName(monitors, isRemote)
        };
    }

    private static string GenerateDefaultName(IReadOnlyList<MonitorInfo> monitors, bool isRemote)
    {
        var prefix = isRemote ? "Remote - " : "";
        return monitors.Count switch
        {
            0 => $"{prefix}No Displays",
            1 => monitors[0].IsBuiltIn
                ? $"{prefix}Laptop Only"
                : $"{prefix}{monitors[0]}",
            _ => $"{prefix}{monitors.Count} Monitors"
        };
    }

    public override string ToString() => Name;
}
