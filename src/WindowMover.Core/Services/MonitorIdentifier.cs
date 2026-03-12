using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WindowMover.Core.Models;
using WindowMover.Core.Native;

namespace WindowMover.Core.Services;

/// <summary>
/// Identifies connected monitors using EDID data retrieved via WMI.
/// Provides stable IDs that survive dock changes and reboots.
/// </summary>
public class MonitorIdentifier
{
    /// <summary>
    /// Retrieves information about all currently connected monitors.
    /// </summary>
    public IReadOnlyList<MonitorInfo> GetConnectedMonitors()
    {
        var edidMonitors = GetMonitorsFromWmi();
        var screens = System.Windows.Forms.Screen.AllScreens;

        // Match WMI-reported monitors to Screen objects by instance name / device name
        var result = new List<MonitorInfo>();

        foreach (var screen in screens)
        {
            var matched = MatchScreenToEdid(screen, edidMonitors);
            if (matched != null)
            {
                PopulateScreenData(matched, screen);
                result.Add(matched);
            }
            else
            {
                // Fallback: create a monitor entry without EDID (e.g., RDP virtual displays)
                result.Add(CreateFallbackMonitor(screen));
            }
        }

        // Disambiguate identical monitors (same EDID) by physical connection
        DisambiguateIdenticalMonitors(result);

        // Sort left-to-right (then top-to-bottom) to match Windows Settings display arrangement
        return result.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }

    private static List<EdidData> GetMonitorsFromWmi()
    {
        var monitors = new List<EdidData>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM WmiMonitorID");

            foreach (var obj in searcher.Get())
            {
                var edid = new EdidData
                {
                    InstanceName = obj["InstanceName"]?.ToString() ?? "",
                    Manufacturer = DecodeWmiArray(obj["ManufacturerName"]),
                    Model = DecodeWmiArray(obj["UserFriendlyName"]),
                    Serial = DecodeWmiArray(obj["SerialNumberID"]),
                    ProductCode = obj["ProductCodeID"]?.ToString() ?? ""
                };

                monitors.Add(edid);
            }
        }
        catch (ManagementException)
        {
            // WMI query can fail in RDP sessions or restricted environments
        }

        return monitors;
    }

    private static string DecodeWmiArray(object? wmiValue)
    {
        if (wmiValue is not ushort[] arr)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var c in arr)
        {
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString().Trim();
    }

    private static MonitorInfo? MatchScreenToEdid(
        System.Windows.Forms.Screen screen, List<EdidData> edidMonitors)
    {
        // Use EnumDisplayDevices to get the PnP model code for this screen,
        // then match against WMI EDID entries by model code for reliable pairing
        var pnpDeviceId = GetMonitorPnPId(screen.DeviceName);
        var pnpModelCode = ExtractModelCode(pnpDeviceId);

        // First pass: match by PnP model code
        if (!string.IsNullOrEmpty(pnpModelCode))
        {
            foreach (var edid in edidMonitors)
            {
                if (edid.Matched) continue;
                var instanceParts = edid.InstanceName.Split('\\');
                if (instanceParts.Length >= 2 &&
                    instanceParts[1].Equals(pnpModelCode, StringComparison.OrdinalIgnoreCase))
                {
                    edid.Matched = true;
                    return CreateMonitorFromEdid(edid, screen);
                }
            }
        }

        // Fallback: accept first unmatched EDID with valid instance name
        foreach (var edid in edidMonitors)
        {
            if (edid.Matched) continue;
            var instanceParts = edid.InstanceName.Split('\\');
            if (instanceParts.Length >= 2)
            {
                edid.Matched = true;
                return CreateMonitorFromEdid(edid, screen);
            }
        }

        return null;
    }

    private static MonitorInfo CreateMonitorFromEdid(EdidData edid, System.Windows.Forms.Screen screen)
    {
        var instanceParts = edid.InstanceName.Split('\\');
        return new MonitorInfo
        {
            DeviceId = $"{edid.Manufacturer}_{edid.ProductCode}_{edid.Serial}".Trim('_'),
            Manufacturer = edid.Manufacturer,
            Model = string.IsNullOrEmpty(edid.Model) ? instanceParts[1] : edid.Model,
            Serial = edid.Serial,
            FriendlyName = string.IsNullOrEmpty(edid.Model)
                ? $"{edid.Manufacturer} ({screen.DeviceName})"
                : edid.Model,
            DevicePath = screen.DeviceName,
            IsBuiltIn = IsBuiltInDisplay(edid.InstanceName)
        };
    }

    /// <summary>
    /// When multiple monitors produce the same EDID-based DeviceId (identical make/model
    /// with missing or identical serial numbers), appends a hash derived from the physical
    /// connection path (via EnumDisplayDevices) to make each DeviceId unique.
    /// </summary>
    private static void DisambiguateIdenticalMonitors(List<MonitorInfo> monitors)
    {
        var groups = monitors
            .GroupBy(m => m.DeviceId)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            foreach (var monitor in group)
            {
                var pnpId = GetMonitorPnPId(monitor.DevicePath);
                if (!string.IsNullOrEmpty(pnpId))
                {
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(pnpId));
                    monitor.DeviceId += $"#{Convert.ToHexString(hash)[..8]}";
                }
                else
                {
                    // Fallback: use screen bounds as disambiguator
                    var bounds = $"{monitor.Bounds.X},{monitor.Bounds.Y},{monitor.Bounds.Width},{monitor.Bounds.Height}";
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(bounds));
                    monitor.DeviceId += $"#P{Convert.ToHexString(hash)[..7]}";
                }
            }

            // Disambiguate display names with position numbers
            int index = 1;
            foreach (var monitor in group.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y))
            {
                monitor.FriendlyName += $" ({index})";
                index++;
            }
        }
    }

    /// <summary>
    /// Gets the PnP Device ID for a monitor via EnumDisplayDevices.
    /// Returns e.g. "MONITOR\HWP3238\{guid}\0003" — unique per physical connection.
    /// </summary>
    private static string GetMonitorPnPId(string deviceName)
    {
        var device = new User32.DISPLAY_DEVICE();
        device.cb = (uint)Marshal.SizeOf<User32.DISPLAY_DEVICE>();

        if (User32.EnumDisplayDevices(deviceName, 0, ref device, 0))
        {
            return device.DeviceID ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// Extracts the model code from a PnP device ID.
    /// E.g., "MONITOR\HWP3238\{guid}\0003" → "HWP3238"
    /// </summary>
    private static string ExtractModelCode(string pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId)) return string.Empty;
        var parts = pnpDeviceId.Split('\\');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private static bool IsBuiltInDisplay(string instanceName)
    {
        // Built-in laptop displays often have instance names containing "LAPTOP" or
        // connect via internal bus types. This heuristic checks common patterns.
        var upper = instanceName.ToUpperInvariant();
        return upper.Contains("LAPTOP") ||
               upper.Contains("_INTERNAL") ||
               upper.Contains("LGD") || // LG Display (common laptop panel OEM)
               upper.Contains("AUO") || // AU Optronics (common laptop panel OEM)
               upper.Contains("BOE") || // BOE Technology (common laptop panel OEM)
               upper.Contains("CMN") || // Chimei Innolux (common laptop panel OEM)
               upper.Contains("SHP");   // Sharp (common laptop panel OEM)
    }

    private static MonitorInfo CreateFallbackMonitor(System.Windows.Forms.Screen screen)
    {
        // For monitors without EDID data (e.g., RDP virtual displays)
        var name = screen.Primary ? "Primary Display" : screen.DeviceName;
        return new MonitorInfo
        {
            DeviceId = $"FALLBACK_{screen.DeviceName.Replace(@"\\.\", "")}_{screen.Bounds.Width}x{screen.Bounds.Height}",
            Manufacturer = "Unknown",
            Model = name,
            FriendlyName = name,
            DevicePath = screen.DeviceName,
            IsBuiltIn = screen.Primary && System.Windows.Forms.Screen.AllScreens.Length == 1,
            CurrentWidth = screen.Bounds.Width,
            CurrentHeight = screen.Bounds.Height,
            WorkArea = screen.WorkingArea,
            Bounds = screen.Bounds
        };
    }

    private static void PopulateScreenData(MonitorInfo info, System.Windows.Forms.Screen screen)
    {
        info.CurrentWidth = screen.Bounds.Width;
        info.CurrentHeight = screen.Bounds.Height;
        info.WorkArea = screen.WorkingArea;
        info.Bounds = screen.Bounds;
    }

    private class EdidData
    {
        public string InstanceName { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string Serial { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public bool Matched { get; set; }
    }
}
