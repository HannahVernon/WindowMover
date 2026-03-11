using System.Text.Json.Serialization;

namespace WindowMover.Core.Models;

/// <summary>
/// Represents a uniquely identifiable physical monitor based on EDID data.
/// </summary>
public class MonitorInfo
{
    /// <summary>
    /// Stable ID derived from EDID (manufacturer + model + serial).
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// EDID manufacturer code (e.g., "DEL" for Dell, "HWP" for HP).
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// EDID product/model name (e.g., "U2720Q").
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// EDID serial number string. May be empty for some monitors.
    /// </summary>
    public string Serial { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly display name (e.g., "Dell U2720Q - Left").
    /// Can be overridden by the user in profile settings.
    /// </summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// Windows device path (e.g., "\\.\DISPLAY1"). Not stable across docks.
    /// </summary>
    [JsonIgnore]
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the laptop's built-in display.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Current horizontal resolution in pixels.
    /// </summary>
    [JsonIgnore]
    public int CurrentWidth { get; set; }

    /// <summary>
    /// Current vertical resolution in pixels.
    /// </summary>
    [JsonIgnore]
    public int CurrentHeight { get; set; }

    /// <summary>
    /// Working area bounds (excludes taskbar), in virtual screen coordinates.
    /// </summary>
    [JsonIgnore]
    public System.Drawing.Rectangle WorkArea { get; set; }

    /// <summary>
    /// Full screen bounds in virtual screen coordinates.
    /// </summary>
    [JsonIgnore]
    public System.Drawing.Rectangle Bounds { get; set; }

    /// <summary>
    /// DPI scaling factor (e.g., 1.0 = 100%, 1.5 = 150%).
    /// </summary>
    [JsonIgnore]
    public double ScaleFactor { get; set; } = 1.0;

    public override string ToString() =>
        string.IsNullOrEmpty(FriendlyName) ? $"{Manufacturer} {Model}" : FriendlyName;

    public override bool Equals(object? obj) =>
        obj is MonitorInfo other && DeviceId == other.DeviceId;

    public override int GetHashCode() => DeviceId.GetHashCode();
}
