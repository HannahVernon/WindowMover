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
    private string _lastFingerprint = string.Empty;
    private bool _disposed;

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
    }

    /// <summary>
    /// Stops watching for display changes.
    /// </summary>
    public void Stop()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounceTimer.Stop();
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
