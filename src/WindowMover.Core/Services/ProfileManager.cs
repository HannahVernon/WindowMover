using System.Text.Json;
using System.Text.Json.Serialization;
using WindowMover.Core.Models;

namespace WindowMover.Core.Services;

/// <summary>
/// Manages loading, saving, and matching layout profiles.
/// Profiles are stored as JSON files in %APPDATA%\WindowMover\profiles\.
/// </summary>
public class ProfileManager
{
    private readonly string _profilesDir;
    private readonly Dictionary<string, LayoutProfile> _profiles = new();
    private readonly JsonSerializerOptions _jsonOptions;

    public ProfileManager() : this(GetDefaultProfilesDir()) { }

    public ProfileManager(string profilesDir)
    {
        _profilesDir = profilesDir;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        Directory.CreateDirectory(_profilesDir);
        LoadAllProfiles();
    }

    /// <summary>
    /// All loaded profiles, keyed by setup fingerprint.
    /// </summary>
    public IReadOnlyDictionary<string, LayoutProfile> Profiles => _profiles;

    /// <summary>
    /// Tries to find a profile matching the given monitor setup.
    /// </summary>
    public LayoutProfile? GetProfile(MonitorSetup setup)
    {
        return _profiles.GetValueOrDefault(setup.Fingerprint);
    }

    /// <summary>
    /// Tries to find a profile matching the given fingerprint.
    /// </summary>
    public LayoutProfile? GetProfile(string fingerprint)
    {
        return _profiles.GetValueOrDefault(fingerprint);
    }

    /// <summary>
    /// Finds the closest existing RDP profile when no exact match exists.
    /// Prefers profiles with the most monitors in common with the current setup.
    /// Returns null if no RDP profiles exist.
    /// </summary>
    public LayoutProfile? FindClosestRemoteProfile(MonitorSetup currentSetup)
    {
        if (!currentSetup.IsRemoteSession)
            return null;

        var currentDeviceIds = new HashSet<string>(
            currentSetup.Monitors.Select(m => m.DeviceId),
            StringComparer.OrdinalIgnoreCase);

        return _profiles.Values
            .Where(p => p.IsRemoteSession)
            .OrderByDescending(p => p.Setup.Monitors
                .Count(m => currentDeviceIds.Contains(m.DeviceId)))
            .ThenByDescending(p => p.LastModified)
            .FirstOrDefault();
    }

    /// <summary>
    /// Creates or updates a profile for the given setup.
    /// </summary>
    public LayoutProfile SaveProfile(MonitorSetup setup, List<WindowRule> rules)
    {
        var profile = new LayoutProfile
        {
            SetupFingerprint = setup.Fingerprint,
            Name = setup.Name,
            IsRemoteSession = setup.IsRemoteSession,
            Setup = setup,
            Rules = rules,
            LastModified = DateTime.UtcNow
        };

        _profiles[setup.Fingerprint] = profile;
        SaveProfileToDisk(profile);
        return profile;
    }

    /// <summary>
    /// Updates just the rules in an existing profile.
    /// </summary>
    public void UpdateRules(string fingerprint, List<WindowRule> rules)
    {
        if (_profiles.TryGetValue(fingerprint, out var profile))
        {
            profile.Rules = rules;
            profile.LastModified = DateTime.UtcNow;
            SaveProfileToDisk(profile);
        }
    }

    /// <summary>
    /// Renames a profile's display name.
    /// </summary>
    public void RenameProfile(string fingerprint, string newName)
    {
        if (_profiles.TryGetValue(fingerprint, out var profile))
        {
            profile.Name = newName;
            profile.Setup.Name = newName;
            profile.LastModified = DateTime.UtcNow;
            SaveProfileToDisk(profile);
        }
    }

    /// <summary>
    /// Deletes a profile.
    /// </summary>
    public void DeleteProfile(string fingerprint)
    {
        if (_profiles.Remove(fingerprint))
        {
            var path = GetProfilePath(fingerprint);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Reloads all profiles from disk.
    /// </summary>
    public void Reload()
    {
        _profiles.Clear();
        LoadAllProfiles();
    }

    private void LoadAllProfiles()
    {
        if (!Directory.Exists(_profilesDir)) return;

        foreach (var file in Directory.GetFiles(_profilesDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<LayoutProfile>(json, _jsonOptions);
                if (profile != null && !string.IsNullOrEmpty(profile.SetupFingerprint))
                {
                    _profiles[profile.SetupFingerprint] = profile;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load profile {file}: {ex.Message}");
            }
        }
    }

    private void SaveProfileToDisk(LayoutProfile profile)
    {
        var path = GetProfilePath(profile.SetupFingerprint);
        var json = JsonSerializer.Serialize(profile, _jsonOptions);
        File.WriteAllText(path, json);
    }

    private string GetProfilePath(string fingerprint)
    {
        // Sanitize fingerprint to prevent path traversal
        var safeName = Path.GetFileName(fingerprint);
        return Path.Combine(_profilesDir, $"{safeName}.json");
    }

    private static string GetDefaultProfilesDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowMover", "profiles");
}
