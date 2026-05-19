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
    private readonly WindowTracker _windowTracker;
    private readonly SemaphoreSlim _setupChangeLock = new(1, 1);
    private int _setupChangeGeneration;

    private string _currentSetupName = "Detecting...";
    private string _statusMessage = string.Empty;
    private MonitorSetup? _currentSetup;
    private string? _activeFingerprint;
    private bool _hasUnsavedChanges;
    private bool _isTopmost;
    private bool _disposed;

    public MainViewModel()
    {
        _monitorIdentifier = new MonitorIdentifier();
        _windowManager = new WindowManager();
        _profileManager = new ProfileManager();
        _monitorWatcher = new MonitorWatcher(_monitorIdentifier);
        _windowMovementWatcher = new WindowMovementWatcher(_monitorIdentifier);
        _windowTracker = new WindowTracker(_windowManager, _monitorIdentifier);

        Monitors = [];
        UnassignedApps = [];

        SaveCommand = new RelayCommand(Save, () => HasUnsavedChanges);
        ApplyNowCommand = new RelayCommand(ApplyNow);
        RefreshAppsCommand = new RelayCommand(RefreshApps);
        ResetCommand = new RelayCommand(Reset);
        CaptureLayoutCommand = new RelayCommand(CaptureCurrentLayout);
        ClearAndCaptureCommand = new RelayCommand(ClearAndCapture);

        _monitorWatcher.SetupChanged += OnSetupChanged;
        _windowMovementWatcher.WindowMoved += OnWindowMoved;
        SessionDetector.SessionChanged += OnSessionChanged;
        Win32WindowHelper.HungWindowDetected += OnHungWindowDetected;
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

    public bool IsTopmost
    {
        get => _isTopmost;
        set => SetProperty(ref _isTopmost, value);
    }

    // Commands
    public RelayCommand SaveCommand { get; }
    public RelayCommand ApplyNowCommand { get; }
    public RelayCommand RefreshAppsCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand CaptureLayoutCommand { get; }
    public RelayCommand ClearAndCaptureCommand { get; }

    /// <summary>
    /// Initializes the ViewModel: detects monitors, loads profile, starts watching.
    /// Heavy work (WMI, window enumeration) runs on a background thread to keep the UI responsive.
    /// </summary>
    public async Task InitializeAsync()
    {
        AppLogger.Instance.Info("Initializing: detecting monitors and loading profile");

        // Attempt to restore window positions from the last snapshot before anything else
        await Task.Run(TryRestoreFromSnapshot);

        await Task.Run(DetectAndLoadSetup);
        _monitorWatcher.Start();
        _windowMovementWatcher.Start();
        SessionDetector.StartWatching();

        // Start periodic desktop snapshots (every 30 seconds)
        _windowTracker.StartPeriodicSnapshots(30);

        AppLogger.Instance.Info($"Initialized with setup: {CurrentSetupName}, {Monitors.Count} monitor(s)");
    }

    /// <summary>
    /// Activates a specific profile by fingerprint: loads its rules into the UI and applies them.
    /// </summary>
    public async void ActivateProfile(string fingerprint)
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
        var windows = await Task.Run(() => _windowManager.GetVisibleWindows());
        RefreshRunningApps(windows);

        // Apply window positions on background thread
        if (_currentSetup != null)
        {
            var setup = _currentSetup;
            _windowMovementWatcher.Suppressed = true;
            try
            {
                await Task.Run(() => _windowManager.ApplyRules(profile.Rules, setup.Monitors, windows));
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
    public void MoveApp(AppRuleViewModel app, MonitorViewModel? sourceMonitor, MonitorViewModel? targetMonitor, int insertIndex = -1)
    {
        // Remove from source
        if (sourceMonitor != null)
            sourceMonitor.AssignedApps.Remove(app);
        else
            UnassignedApps.Remove(app);

        // Add to target at the requested position
        if (targetMonitor != null)
        {
            if (insertIndex >= 0 && insertIndex <= targetMonitor.AssignedApps.Count)
                targetMonitor.AssignedApps.Insert(insertIndex, app);
            else
                targetMonitor.AssignedApps.Add(app);
        }
        else
        {
            UnassignedApps.Add(app);
        }

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
        // Phase 1: Heavy work (WMI, window enumeration) — safe to call from any thread
        var monitors = _monitorIdentifier.GetConnectedMonitors();
        var isRemote = SessionDetector.IsRemoteSession();
        _currentSetup = MonitorSetup.FromMonitors(monitors, isRemote);
        var allFallback = MonitorIdentifier.AllMonitorsAreFallback(monitors);

        // Pre-enumerate windows off the UI thread so the dispatcher block stays fast
        var visibleWindows = _windowManager.GetVisibleWindows();

        // Phase 2: UI updates — must run on UI thread for ObservableCollection mutations
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentSetupName = _currentSetup.Name;
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
            else if (allFallback)
            {
                // WMI/EDID data is unavailable — show current windows but don't save
                // a profile with fallback IDs that would be wrong once EDID loads.
                AppLogger.Instance.Warn("All monitors using fallback IDs — deferring profile creation until EDID data is available");
                RefreshRunningApps(visibleWindows);
                StatusMessage = "Waiting for monitor identification...";
                return;
            }
            else if (isRemote)
            {
                // For RDP sessions with no exact match, try to reuse the closest
                // existing RDP profile rather than auto-creating a new one.
                var closest = _profileManager.FindClosestRemoteProfile(_currentSetup);
                if (closest != null)
                {
                    _activeFingerprint = closest.SetupFingerprint;
                    CurrentSetupName = $"{closest.Name} (adapted)";
                    LoadProfileRules(closest);
                    // Move apps assigned to monitors that no longer exist to the first monitor
                    ReassignOrphanedApps();
                    AppLogger.Instance.Info($"Adapted RDP profile: {closest.Name} for current {monitors.Count}-monitor session");
                    StatusMessage = $"Adapted profile: {closest.Name}";
                }
                else
                {
                    var capturedRules = _windowManager.CaptureCurrentLayout(_currentSetup.Monitors, visibleWindows);
                    var newProfile = _profileManager.SaveProfile(_currentSetup, capturedRules);
                    _activeFingerprint = newProfile.SetupFingerprint;
                    CurrentSetupName = newProfile.Name;
                    LoadProfileRules(newProfile);
                    AppLogger.Instance.Info($"Auto-created RDP profile: {newProfile.Name} with {capturedRules.Count} rule(s)");
                    StatusMessage = $"New profile created: {newProfile.Name}";
                }
            }
            else
            {
                // Auto-create a new profile from the current window layout
                var capturedRules = _windowManager.CaptureCurrentLayout(_currentSetup.Monitors, visibleWindows);
                var newProfile = _profileManager.SaveProfile(_currentSetup, capturedRules);
                _activeFingerprint = newProfile.SetupFingerprint;
                CurrentSetupName = newProfile.Name;
                LoadProfileRules(newProfile);
                AppLogger.Instance.Info($"Auto-created profile: {newProfile.Name} with {capturedRules.Count} rule(s)");
                StatusMessage = $"New profile created: {newProfile.Name}";
            }

            // Add running apps that aren't in any rule
            RefreshRunningApps(visibleWindows);
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

    /// <summary>
    /// Moves any unassigned apps (from disconnected monitors) to the first available monitor.
    /// Used when adapting an RDP profile to a session with fewer monitors.
    /// </summary>
    private void ReassignOrphanedApps()
    {
        if (Monitors.Count == 0 || UnassignedApps.Count == 0)
            return;

        var primaryMonitor = Monitors[0];
        var orphans = UnassignedApps.ToList();
        foreach (var app in orphans)
        {
            UnassignedApps.Remove(app);
            primaryMonitor.AssignedApps.Add(app);
        }

        if (orphans.Count > 0)
            AppLogger.Instance.Info($"Reassigned {orphans.Count} app(s) from disconnected monitors to {primaryMonitor.DisplayName}");
    }

    private void RefreshRunningApps(List<WindowInfo>? preEnumeratedWindows = null)
    {
        var visibleWindows = preEnumeratedWindows ?? _windowManager.GetVisibleWindows();
        var assignedHandles = new HashSet<IntPtr>(
            Monitors.SelectMany(m => m.AssignedApps)
                .Concat(UnassignedApps)
                .Where(a => a.WindowHandle != IntPtr.Zero)
                .Select(a => a.WindowHandle));

        // Also track assigned by ProcessName+Title for profile-loaded rules (no handle)
        var assignedKeys = new HashSet<string>(
            Monitors.SelectMany(m => m.AssignedApps)
                .Concat(UnassignedApps)
                .Select(a => $"{a.ProcessName}|{a.WindowTitle}"),
            StringComparer.OrdinalIgnoreCase);

        foreach (var window in visibleWindows)
        {
            if (assignedHandles.Contains(window.Handle))
                continue;

            var key = $"{window.ProcessName}|{window.Title}";
            if (assignedKeys.Contains(key))
                continue;

            UnassignedApps.Add(new AppRuleViewModel(window));
            assignedHandles.Add(window.Handle);
            assignedKeys.Add(key);
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

    private async void ApplyNow()
    {
        if (_currentSetup == null) return;

        if (HasUnsavedChanges)
        {
            Save();
            StatusMessage = "Changes saved automatically";
        }

        IsTopmost = true;
        _windowMovementWatcher.Suppressed = true;
        try
        {
            var profile = _activeFingerprint != null
                ? _profileManager.GetProfile(_activeFingerprint)
                : _profileManager.GetProfile(_currentSetup);

            var setup = _currentSetup;
            if (profile != null)
            {
                await Task.Run(() => _windowManager.ApplyRules(profile.Rules, setup.Monitors));
                StatusMessage = "Rules saved and applied";
            }
            else
            {
                var rules = Monitors
                    .SelectMany(m => m.AssignedApps.Select(a => a.ToRule(m.DeviceId)))
                    .ToList();
                await Task.Run(() => _windowManager.ApplyRules(rules, setup.Monitors));
                StatusMessage = "Rules applied (unsaved)";
            }
        }
        finally
        {
            _windowMovementWatcher.Suppressed = false;
            IsTopmost = false;
        }
    }

    private async void RefreshApps()
    {
        var windows = await Task.Run(() => _windowManager.GetVisibleWindows());
        RefreshRunningApps(windows);
        StatusMessage = "App list refreshed";
    }

    private async void Reset()
    {
        await Task.Run(DetectAndLoadSetup);
        HasUnsavedChanges = false;
        StatusMessage = "Reset to saved profile";
    }

    private async void CaptureCurrentLayout()
    {
        if (_currentSetup == null) return;

        var monitors = _currentSetup.Monitors;
        var capturedRules = await Task.Run(() => _windowManager.CaptureCurrentLayout(monitors));
        AppLogger.Instance.Info($"Captured current layout: {capturedRules.Count} app(s)");

        // Build a lookup of captured rules keyed by (ProcessName, WindowTitle) for upsert
        var capturedLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in capturedRules)
        {
            var key = MakeRuleKey(rule.ProcessName, rule.WindowTitle);
            capturedLookup.Add(key);
        }

        // Remove existing rules that match a captured rule (they will be replaced).
        // Rules for apps that are NOT currently open are preserved.
        foreach (var monitor in Monitors)
        {
            var toRemove = monitor.AssignedApps
                .Where(a => capturedLookup.Contains(MakeRuleKey(a.ProcessName, a.WindowTitle)))
                .ToList();
            foreach (var app in toRemove)
                monitor.AssignedApps.Remove(app);
        }

        var unassignedToRemove = UnassignedApps
            .Where(a => capturedLookup.Contains(MakeRuleKey(a.ProcessName, a.WindowTitle)))
            .ToList();
        foreach (var app in unassignedToRemove)
            UnassignedApps.Remove(app);

        // Add the freshly captured rules
        foreach (var rule in capturedRules)
        {
            var monitorVm = Monitors.FirstOrDefault(m => m.DeviceId == rule.TargetMonitorId);
            if (monitorVm != null)
                monitorVm.AssignedApps.Add(new AppRuleViewModel(rule));
            else
                UnassignedApps.Add(new AppRuleViewModel(rule));
        }

        HasUnsavedChanges = true;

        // Count total rules across all monitors + unassigned
        var totalRules = Monitors.Sum(m => m.AssignedApps.Count) + UnassignedApps.Count;
        StatusMessage = $"Captured {capturedRules.Count} app(s), {totalRules} total rules in profile";
    }

    private async void ClearAndCapture()
    {
        if (_currentSetup == null) return;

        var monitors = _currentSetup.Monitors;
        var rules = await Task.Run(() => _windowManager.CaptureCurrentLayout(monitors));
        AppLogger.Instance.Info($"Clear & Capture: {rules.Count} app(s)");

        // Replace: clear everything and repopulate from captured state only
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
        StatusMessage = $"Cleared profile and captured {rules.Count} app(s)";
    }

    private static string MakeRuleKey(string processName, string? windowTitle)
    {
        return string.IsNullOrEmpty(windowTitle)
            ? processName
            : $"{processName}\0{windowTitle}";
    }

    private async void OnSetupChanged(object? sender, SetupChangedEventArgs e)
    {
        AppLogger.Instance.Info($"Monitor setup changed: {e.NewSetup.Name} ({e.NewSetup.Monitors.Count} monitors)");

        // Generation counter for event coalescing — if a newer event arrives while
        // we're waiting for the lock, the older event skips its work.
        var myGeneration = Interlocked.Increment(ref _setupChangeGeneration);

        // Suppress window-move tracking before any work starts
        _windowMovementWatcher.Suppressed = true;

        await _setupChangeLock.WaitAsync();
        try
        {
            // A newer event arrived while we waited — let it handle the update
            if (myGeneration != Volatile.Read(ref _setupChangeGeneration))
                return;

            // Heavy work (WMI, window enumeration) on a background thread
            await Task.Run(() =>
            {
                DetectAndLoadSetup();

                // Bail if a newer event arrived during detection
                if (myGeneration != Volatile.Read(ref _setupChangeGeneration))
                    return;

                // Apply rules — SetWindowPos doesn't require the WPF dispatcher
                if (_currentSetup != null)
                {
                    var profile = _profileManager.GetProfile(_currentSetup);
                    if (profile != null)
                    {
                        _windowManager.ApplyRules(profile.Rules, _currentSetup.Monitors);
                        Application.Current.Dispatcher.BeginInvoke(() =>
                            StatusMessage = $"Setup changed to '{_currentSetup.Name}' — rules applied");
                    }
                    else
                    {
                        Application.Current.Dispatcher.BeginInvoke(() =>
                            StatusMessage = $"New setup detected: {_currentSetup?.Name}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error("Error handling setup change", ex);
        }
        finally
        {
            _setupChangeLock.Release();
            _windowMovementWatcher.Suppressed = false;
        }
    }

    private async void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        // Re-detect everything when session type changes — same async pattern
        var myGeneration = Interlocked.Increment(ref _setupChangeGeneration);
        _windowMovementWatcher.Suppressed = true;

        await _setupChangeLock.WaitAsync();
        try
        {
            if (myGeneration != Volatile.Read(ref _setupChangeGeneration))
                return;

            await Task.Run(DetectAndLoadSetup);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error("Error handling session change", ex);
        }
        finally
        {
            _setupChangeLock.Release();
            _windowMovementWatcher.Suppressed = false;
        }
    }

    private void OnHungWindowDetected(object? sender, HungWindowEventArgs e)
    {
        var displayName = !string.IsNullOrWhiteSpace(e.WindowTitle)
            ? $"\"{e.WindowTitle}\" ({e.ProcessName})"
            : e.ProcessName;

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            StatusMessage = $"⚠ Skipped unresponsive window: {displayName}";
        });
    }

    /// <summary>
    /// On startup, attempts to restore window positions from the last periodic snapshot.
    /// Only restores if the current monitor setup matches the snapshot's setup.
    /// </summary>
    private void TryRestoreFromSnapshot()
    {
        try
        {
            var snapshot = _windowTracker.LoadLastSnapshot();
            if (snapshot == null)
            {
                AppLogger.Instance.Info("No previous snapshot found — skipping restore");
                return;
            }

            // Check if the snapshot is too old (more than 24 hours)
            if ((DateTime.UtcNow - snapshot.CapturedAt).TotalHours > 24)
            {
                AppLogger.Instance.Info($"Snapshot is too old ({snapshot.CapturedAt:g}) — skipping restore");
                return;
            }

            // Check machine name matches
            if (!snapshot.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Instance.Info("Snapshot is from a different machine — skipping restore");
                return;
            }

            var monitors = _monitorIdentifier.GetConnectedMonitors();
            var isRemote = SessionDetector.IsRemoteSession();
            var currentFingerprint = MonitorSetup.ComputeFingerprint(monitors, isRemote);

            if (!currentFingerprint.Equals(snapshot.SetupFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Instance.Info("Monitor setup changed since last snapshot — skipping restore");
                return;
            }

            var restored = _windowTracker.RestoreFromSnapshot(snapshot, monitors);
            StatusMessage = restored > 0
                ? $"Restored {restored} window(s) from last session"
                : "No windows matched for restoration";
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error("Failed to restore from snapshot", ex);
        }
    }

    private void OnWindowMoved(object? sender, WindowMovedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var targetMonitorVm = Monitors.FirstOrDefault(
                m => m.DeviceId == e.TargetMonitor.DeviceId);

            if (targetMonitorVm == null)
                return;

            // Find existing card for this specific window (by handle first, then process+title)
            AppRuleViewModel? existingApp = null;
            MonitorViewModel? sourceMonitor = null;

            foreach (var monitor in Monitors)
            {
                existingApp = monitor.AssignedApps.FirstOrDefault(a => a.WindowHandle == e.WindowHandle)
                    ?? monitor.AssignedApps.FirstOrDefault(a =>
                        a.ProcessName.Equals(e.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                        a.WindowTitle.Equals(e.WindowTitle, StringComparison.OrdinalIgnoreCase));
                if (existingApp != null)
                {
                    sourceMonitor = monitor;
                    break;
                }
            }

            existingApp ??= UnassignedApps.FirstOrDefault(a => a.WindowHandle == e.WindowHandle)
                ?? UnassignedApps.FirstOrDefault(a =>
                    a.ProcessName.Equals(e.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                    a.WindowTitle.Equals(e.WindowTitle, StringComparison.OrdinalIgnoreCase));

            if (existingApp != null)
            {
                if (sourceMonitor == targetMonitorVm)
                    return;

                // Update handle and title in case they changed
                existingApp.WindowHandle = e.WindowHandle;
                existingApp.WindowTitle = e.WindowTitle;

                MoveApp(existingApp, sourceMonitor, targetMonitorVm);
                StatusMessage = $"{existingApp.DisplayName} moved to {targetMonitorVm.DisplayName}";
            }
            else
            {
                // New window we haven't seen — add it directly to the target monitor
                var newApp = new AppRuleViewModel
                {
                    ProcessName = e.ProcessName,
                    DisplayName = e.ProcessName,
                    WindowTitle = e.WindowTitle,
                    ExecutablePath = e.ExecutablePath,
                    WindowHandle = e.WindowHandle
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
        Win32WindowHelper.HungWindowDetected -= OnHungWindowDetected;
        _windowTracker.Dispose();  // saves final snapshot
        _monitorWatcher.Dispose();
        _windowMovementWatcher.Dispose();
        SessionDetector.StopWatching();
        _setupChangeLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
