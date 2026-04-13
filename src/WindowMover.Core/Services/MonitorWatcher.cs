using System.Timers;
using WindowMover.Core.Models;
using Microsoft.Win32;

namespace WindowMover.Core.Services;

/// <summary>
/// Watches for display configuration changes (monitor connect/disconnect)
/// and raises events after a debounce period to allow Windows to stabilize.
/// </summary>
public class MonitorWatcher : IDisposable
{
    private readonly MonitorIdentifier _monitorIdentifier;
    private readonly System.Timers.Timer _debounceTimer;
    private System.Timers.Timer? _powerResumeTimer;
    private string _lastFingerprint = string.Empty;
    private bool _disposed;
    private int _powerResumeRetryCount;

    private const int PowerResumeMaxRetries = 5;
    private const int PowerResumeInitialDelayMs = 5000;

    /// <summary>
    /// Raised when the monitor setup has changed (after debounce).
    /// </summary>
    public event EventHandler<SetupChangedEventArgs>? SetupChanged;

    /// <summary>
    /// Debounce interval in milliseconds. Default is 3000ms (3 seconds).
    /// </summary>
    public double DebounceMs
    {
        get => _debounceTimer.Interval;
        set => _debounceTimer.Interval = value;
    }

    public MonitorWatcher(MonitorIdentifier monitorIdentifier)
    {
        _monitorIdentifier = monitorIdentifier;
        _debounceTimer = new System.Timers.Timer(3000) { AutoReset = false };
        _debounceTimer.Elapsed += OnDebounceElapsed;
    }

    /// <summary>
    /// Starts watching for display changes.
    /// </summary>
    public void Start()
    {
        // Capture initial state
        var monitors = _monitorIdentifier.GetConnectedMonitors();
        var isRemote = SessionDetector.IsRemoteSession();
        _lastFingerprint = MonitorSetup.ComputeFingerprint(monitors, isRemote);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>
    /// Stops watching for display changes.
    /// </summary>
    public void Stop()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _debounceTimer.Stop();
        _powerResumeTimer?.Stop();
    }

    /// <summary>
    /// Forces re-evaluation of the current monitor setup.
    /// </summary>
    public void ForceRefresh()
    {
        _debounceTimer.Stop();
        EvaluateSetup();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Restart the debounce timer on each change event
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            AppLogger.Instance.Info("Power resume detected — scheduling delayed monitor re-evaluation");

            // Cancel any pending normal debounce; the resume timer takes priority
            _debounceTimer.Stop();
            _powerResumeRetryCount = 0;
            SchedulePowerResumeRetry(PowerResumeInitialDelayMs);
        }
    }

    private void SchedulePowerResumeRetry(int delayMs)
    {
        _powerResumeTimer?.Stop();
        _powerResumeTimer?.Dispose();

        _powerResumeTimer = new System.Timers.Timer(delayMs) { AutoReset = false };
        _powerResumeTimer.Elapsed += OnPowerResumeTimerElapsed;
        _powerResumeTimer.Start();
    }

    private void OnPowerResumeTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        _powerResumeRetryCount++;
        AppLogger.Instance.Info($"Power resume re-evaluation attempt {_powerResumeRetryCount}/{PowerResumeMaxRetries}");

        var monitors = _monitorIdentifier.GetConnectedMonitorsWithRetry(maxRetries: 1, retryDelayMs: 1000);
        var isRemote = SessionDetector.IsRemoteSession();
        var allFallback = MonitorIdentifier.AllMonitorsAreFallback(monitors);

        if (allFallback && _powerResumeRetryCount < PowerResumeMaxRetries)
        {
            // WMI still not ready — retry with increasing delay
            int nextDelay = Math.Min(PowerResumeInitialDelayMs * (int)Math.Pow(2, _powerResumeRetryCount), 30000);
            AppLogger.Instance.Info($"WMI still returning fallback IDs, retrying in {nextDelay}ms");
            SchedulePowerResumeRetry(nextDelay);
            return;
        }

        // Either we got real EDID data, or we've exhausted retries
        var fingerprint = MonitorSetup.ComputeFingerprint(monitors, isRemote);
        if (allFallback)
        {
            AppLogger.Instance.Warn("Exhausted power resume retries — using fallback monitor IDs");
        }

        // Always fire setup changed after power resume to ensure correct profile loads
        _lastFingerprint = fingerprint;
        var setup = MonitorSetup.FromMonitors(monitors, isRemote);
        SetupChanged?.Invoke(this, new SetupChangedEventArgs(setup));
    }

    private void OnDebounceElapsed(object? sender, ElapsedEventArgs e)
    {
        EvaluateSetup();
    }

    private void EvaluateSetup()
    {
        try
        {
            var monitors = _monitorIdentifier.GetConnectedMonitors();
            var isRemote = SessionDetector.IsRemoteSession();
            var fingerprint = MonitorSetup.ComputeFingerprint(monitors, isRemote);

            if (fingerprint != _lastFingerprint)
            {
                _lastFingerprint = fingerprint;
                var setup = MonitorSetup.FromMonitors(monitors, isRemote);
                SetupChanged?.Invoke(this, new SetupChangedEventArgs(setup));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MonitorWatcher error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _debounceTimer.Dispose();
        _powerResumeTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class SetupChangedEventArgs : EventArgs
{
    public MonitorSetup NewSetup { get; }

    public SetupChangedEventArgs(MonitorSetup newSetup)
    {
        NewSetup = newSetup;
    }
}
