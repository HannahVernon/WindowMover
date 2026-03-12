using System.Management;
using System.Text;
using WindowMover.Core.Models;

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
        // Try to match by device name correlation
        // Screen.DeviceName is like "\\.\DISPLAY1"
        var displayIndex = ExtractDisplayIndex(screen.DeviceName);

        foreach (var edid in edidMonitors)
        {
            // WMI InstanceName contains the display path, e.g., "DISPLAY\DEL40A3\..."
            if (edid.Matched) continue;

            // Heuristic: match by index ordering
            // More reliable matching could use SetupAPI, but WMI ordering is usually consistent
            var instanceParts = edid.InstanceName.Split('\\');
            if (instanceParts.Length >= 2)
            {
                edid.Matched = true;
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
        }

        return null;
    }

    private static int ExtractDisplayIndex(string deviceName)
    {
        // "\\.\DISPLAY1" → 1
        var num = deviceName.Replace(@"\\.\DISPLAY", "");
        return int.TryParse(num, out var index) ? index : 0;
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
