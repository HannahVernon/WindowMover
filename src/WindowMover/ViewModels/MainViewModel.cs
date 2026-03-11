using System.Collections.ObjectModel;
using System.Windows;
using WindowMover.Core.Models;
using WindowMover.Core.Services;

namespace WindowMover.ViewModels;

/// <summary>
/// Main ViewModel orchestrating the monitor layout editor.
/// </summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly MonitorIdentifier _monitorIdentifier;
    private readonly MonitorWatcher _monitorWatcher;
    private readonly WindowManager _windowManager;
    private readonly ProfileManager _profileManager;
    private readonly WindowMovementWatcher _windowMovementWatcher;

    private string _currentSetupName = "Detecting...";
    private string _statusMessage = string.Empty;
    private MonitorSetup? _currentSetup;
    private string? _activeFingerprint;
    private bool _hasUnsavedChanges;
    private bool _disposed;

    public MainViewModel()
    {
        _monitorIdentifier = new MonitorIdentifier();
        _windowManager = new WindowManager();
        _profileManager = new ProfileManager();
        _monitorWatcher = new MonitorWatcher(_monitorIdentifier);
        _windowMovementWatcher = new WindowMovementWatcher(_monitorIdentifier);

        Monitors = [];
        UnassignedApps = [];

        SaveCommand = new RelayCommand(Save, () => HasUnsavedChanges);
        ApplyNowCommand = new RelayCommand(ApplyNow);
        RefreshAppsCommand = new RelayCommand(RefreshApps);
        ResetCommand = new RelayCommand(Reset);
        CaptureLayoutCommand = new RelayCommand(CaptureCurrentLayout);

        _monitorWatcher.SetupChanged += OnSetupChanged;
        _windowMovementWatcher.WindowMoved += OnWindowMoved;
        SessionDetector.SessionChanged += OnSessionChanged;
    }

    // Collections
    public ObservableCollection<MonitorViewModel> Monitors { get; }
    public ObservableCollection<AppRuleViewModel> UnassignedApps { get; }

    // Properties
    public ProfileManager ProfileManager => _profileManager;
    public string? ActiveFingerprint => _activeFingerprint;

    public string CurrentSetupName
    {
        get => _currentSetupName;
        set => SetProperty(ref _currentSetupName, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set
        {
            SetProperty(ref _hasUnsavedChanges, value);
            OnPropertyChanged(nameof(UnsavedIndicator));
        }
    }

    public string UnsavedIndicator => HasUnsavedChanges ? "Unsaved Changes" : "No Unsaved Changes";

    // Commands
    public RelayCommand SaveCommand { get; }
    public RelayCommand ApplyNowCommand { get; }
    public RelayCommand RefreshAppsCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand CaptureLayoutCommand { get; }

    /// <summary>
    /// Initializes the ViewModel: detects monitors, loads profile, starts watching.
    /// </summary>
    public void Initialize()
    {
        AppLogger.Instance.Info("Initializing: detecting monitors and loading profile");
        DetectAndLoadSetup();
        _monitorWatcher.Start();
        _windowMovementWatcher.Start();
        SessionDetector.StartWatching();
        AppLogger.Instance.Info($"Initialized with setup: {CurrentSetupName}, {Monitors.Count} monitor(s)");
    }

    /// <summary>
    /// Activates a specific profile by fingerprint: loads its rules into the UI and applies them.
    /// </summary>
    public void ActivateProfile(string fingerprint)
    {
        var profile = _profileManager.GetProfile(fingerprint);
        if (profile == null) return;

        AppLogger.Instance.Info($"Activating profile: {profile.Name} ({fingerprint})");

        _activeFingerprint = fingerprint;
        // Reload the UI with this profile's rules
        foreach (var monitor in Monitors)
            monitor.AssignedApps.Clear();
        UnassignedApps.Clear();

        LoadProfileRules(profile);
        RefreshRunningApps();

        // Apply window positions
        if (_currentSetup != null)
        {
            _windowMovementWatcher.Suppressed = true;
            try
            {
                _windowManager.ApplyRules(profile.Rules, _currentSetup.Monitors);
            }
            finally
            {
                _windowMovementWatcher.Suppressed = false;
            }
        }

        CurrentSetupName = profile.Name;
        HasUnsavedChanges = false;
        StatusMessage = $"Activated profile: {profile.Name}";
    }

    /// <summary>
    /// Moves an app from one container to another (drag-and-drop handler).
    /// </summary>
    public void MoveApp(AppRuleViewModel app, MonitorViewModel? sourceMonitor, MonitorViewModel? targetMonitor)
    {
        // Remove from source
        if (sourceMonitor != null)
            sourceMonitor.AssignedApps.Remove(app);
        else
            UnassignedApps.Remove(app);

        // Add to target
        if (targetMonitor != null)
            targetMonitor.AssignedApps.Add(app);
        else
            UnassignedApps.Add(app);

        HasUnsavedChanges = true;
    }

    /// <summary>
    /// Moves an app back to the unassigned pool.
    /// </summary>
    public void UnassignApp(AppRuleViewModel app, MonitorViewModel sourceMonitor)
    {
        sourceMonitor.AssignedApps.Remove(app);
        UnassignedApps.Add(app);
        HasUnsavedChanges = true;
    }

    private void DetectAndLoadSetup()
    {
        var monitors = _monitorIdentifier.GetConnectedMonitors();
        var isRemote = SessionDetector.IsRemoteSession();
        _currentSetup = MonitorSetup.FromMonitors(monitors, isRemote);

        CurrentSetupName = _currentSetup.Name;

        // Rebuild monitor columns
        Application.Current.Dispatcher.Invoke(() =>
        {
            Monitors.Clear();
            UnassignedApps.Clear();

            foreach (var monitor in monitors)
            {
                Monitors.Add(new MonitorViewModel(monitor));
            }

            // Load existing profile or start fresh
            var profile = _profileManager.GetProfile(_currentSetup);
            if (profile != null)
            {
                _activeFingerprint = profile.SetupFingerprint;
                CurrentSetupName = profile.Name;
                LoadProfileRules(profile);
                StatusMessage = $"Loaded profile: {profile.Name}";
            }
            else
            {
                // Auto-create a new profile from the current window layout
                var capturedRules = _windowManager.CaptureCurrentLayout(_currentSetup.Monitors);
                var newProfile = _profileManager.SaveProfile(_currentSetup, capturedRules);
                _activeFingerprint = newProfile.SetupFingerprint;
                CurrentSetupName = newProfile.Name;
                LoadProfileRules(newProfile);
                AppLogger.Instance.Info($"Auto-created profile: {newProfile.Name} with {capturedRules.Count} rule(s)");
                StatusMessage = $"New profile created: {newProfile.Name}";
            }

            // Add running apps that aren't in any rule
            RefreshRunningApps();
        });
    }

    private void LoadProfileRules(LayoutProfile profile)
    {
        foreach (var rule in profile.Rules)
        {
            var monitorVm = Monitors.FirstOrDefault(m => m.DeviceId == rule.TargetMonitorId);
            if (monitorVm != null)
            {
                monitorVm.AssignedApps.Add(new AppRuleViewModel(rule));
            }
            else
            {
                // Monitor no longer connected — put in unassigned
                UnassignedApps.Add(new AppRuleViewModel(rule));
            }
        }
    }

    private void RefreshRunningApps()
    {
        var runningApps = _windowManager.GetRunningApps();
        var assignedProcesses = new HashSet<string>(
            Monitors.SelectMany(m => m.AssignedApps)
                .Concat(UnassignedApps)
                .Select(a => a.ProcessName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var app in runningApps)
        {
            if (!assignedProcesses.Contains(app.ProcessName))
            {
                UnassignedApps.Add(new AppRuleViewModel(app));
            }
        }
    }

    private void Save()
    {
        if (_currentSetup == null || _activeFingerprint == null) return;

        var rules = Monitors
            .SelectMany(m => m.AssignedApps.Select(a => a.ToRule(m.DeviceId)))
            .ToList();

        // Update the active profile's rules (which may differ from _currentSetup.Fingerprint)
        var existingProfile = _profileManager.GetProfile(_activeFingerprint);
        if (existingProfile != null)
        {
            _profileManager.UpdateRules(_activeFingerprint, rules);
            HasUnsavedChanges = false;
            StatusMessage = $"Profile saved: {existingProfile.Name}";
        }
        else
        {
            // Fallback: create a new profile from the current setup
            _profileManager.SaveProfile(_currentSetup, rules);
            _activeFingerprint = _currentSetup.Fingerprint;
            HasUnsavedChanges = false;
            StatusMessage = $"Profile saved: {_currentSetup.Name}";
        }
    }

    private void ApplyNow()
    {
        if (_currentSetup == null) return;

        if (HasUnsavedChanges)
        {
            Save();
            StatusMessage = "Changes saved automatically";
        }

        _windowMovementWatcher.Suppressed = true;
        try
        {
            var profile = _activeFingerprint != null
                ? _profileManager.GetProfile(_activeFingerprint)
                : _profileManager.GetProfile(_currentSetup);

            if (profile != null)
            {
                _windowManager.ApplyRules(profile.Rules, _currentSetup.Monitors);
                StatusMessage = "Rules saved and applied";
            }
            else
            {
                var rules = Monitors
                    .SelectMany(m => m.AssignedApps.Select(a => a.ToRule(m.DeviceId)))
                    .ToList();
                _windowManager.ApplyRules(rules, _currentSetup.Monitors);
                StatusMessage = "Rules applied (unsaved)";
            }
        }
        finally
        {
            _windowMovementWatcher.Suppressed = false;
        }
    }

    private void RefreshApps()
    {
        RefreshRunningApps();
        StatusMessage = "App list refreshed";
    }

    private void Reset()
    {
        DetectAndLoadSetup();
        HasUnsavedChanges = false;
        StatusMessage = "Reset to saved profile";
    }

    private void CaptureCurrentLayout()
    {
        if (_currentSetup == null) return;

        var rules = _windowManager.CaptureCurrentLayout(_currentSetup.Monitors);
        AppLogger.Instance.Info($"Captured current layout: {rules.Count} app(s)");

        // Clear existing assignments and repopulate from captured state
        foreach (var monitor in Monitors)
            monitor.AssignedApps.Clear();
        UnassignedApps.Clear();

        foreach (var rule in rules)
        {
            var monitorVm = Monitors.FirstOrDefault(m => m.DeviceId == rule.TargetMonitorId);
            if (monitorVm != null)
                monitorVm.AssignedApps.Add(new AppRuleViewModel(rule));
            else
                UnassignedApps.Add(new AppRuleViewModel(rule));
        }

        HasUnsavedChanges = true;
        StatusMessage = $"Captured current layout — {rules.Count} app(s) mapped";
    }

    private void OnSetupChanged(object? sender, SetupChangedEventArgs e)
    {
        AppLogger.Instance.Info($"Monitor setup changed: {e.NewSetup.Name} ({e.NewSetup.Monitors.Count} monitors)");
        Application.Current.Dispatcher.Invoke(() =>
        {
            _windowMovementWatcher.Suppressed = true;
            try
            {
                DetectAndLoadSetup();

                // Auto-apply rules if a profile exists for the new setup
                var profile = _profileManager.GetProfile(e.NewSetup);
                if (profile != null)
                {
                    _windowManager.ApplyRules(profile.Rules, e.NewSetup.Monitors);
                    StatusMessage = $"Setup changed to '{e.NewSetup.Name}' — rules applied";
                }
                else
                {
                    StatusMessage = $"New setup detected: {e.NewSetup.Name}";
                }
            }
            finally
            {
                _windowMovementWatcher.Suppressed = false;
            }
        });
    }

    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        // Re-detect everything when session type changes
        Application.Current.Dispatcher.Invoke(DetectAndLoadSetup);
    }

    private void OnWindowMoved(object? sender, WindowMovedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var targetMonitorVm = Monitors.FirstOrDefault(
                m => m.DeviceId == e.TargetMonitor.DeviceId);

            if (targetMonitorVm == null)
                return;

            // Find existing rule for this process in any monitor column or unassigned
            AppRuleViewModel? existingApp = null;
            MonitorViewModel? sourceMonitor = null;

            foreach (var monitor in Monitors)
            {
                existingApp = monitor.AssignedApps.FirstOrDefault(
                    a => a.ProcessName.Equals(e.ProcessName, StringComparison.OrdinalIgnoreCase));
                if (existingApp != null)
                {
                    sourceMonitor = monitor;
                    break;
                }
            }

            existingApp ??= UnassignedApps.FirstOrDefault(
                a => a.ProcessName.Equals(e.ProcessName, StringComparison.OrdinalIgnoreCase));

            if (existingApp != null)
            {
                // Already on the correct monitor — nothing to do
                if (sourceMonitor == targetMonitorVm)
                    return;

                MoveApp(existingApp, sourceMonitor, targetMonitorVm);
                StatusMessage = $"{existingApp.DisplayName} moved to {targetMonitorVm.DisplayName}";
            }
            else
            {
                // New app we haven't seen — add it directly to the target monitor
                var newApp = new AppRuleViewModel
                {
                    ProcessName = e.ProcessName,
                    DisplayName = e.ProcessName,
                    ExecutablePath = e.ExecutablePath
                };
                targetMonitorVm.AssignedApps.Add(newApp);
                HasUnsavedChanges = true;
                StatusMessage = $"{e.ProcessName} assigned to {targetMonitorVm.DisplayName}";
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitorWatcher.SetupChanged -= OnSetupChanged;
        _windowMovementWatcher.WindowMoved -= OnWindowMoved;
        SessionDetector.SessionChanged -= OnSessionChanged;
        _monitorWatcher.Dispose();
        _windowMovementWatcher.Dispose();
        SessionDetector.StopWatching();

        GC.SuppressFinalize(this);
    }
}
