using WindowMover.Core.Native;
using Microsoft.Win32;

namespace WindowMover.Core.Services;

/// <summary>
/// Detects whether the current session is a local console session or a remote (RDP) session.
/// Monitors session changes to detect RDP connect/disconnect.
/// </summary>
public static class SessionDetector
{
    /// <summary>
    /// Raised when the session type changes (e.g., local → RDP or RDP → local).
    /// </summary>
    public static event EventHandler<SessionChangedEventArgs>? SessionChanged;

    private static bool _lastRemoteState;
    private static bool _watching;

    /// <summary>
    /// Returns true if the current session is a remote (RDP/terminal services) session.
    /// </summary>
    public static bool IsRemoteSession()
    {
        // Primary check: WinForms built-in property
        if (System.Windows.Forms.SystemInformation.TerminalServerSession)
            return true;

        // Secondary check: WTS client protocol type
        try
        {
            if (Wtsapi32.WTSQuerySessionInformationW(
                    Wtsapi32.WTS_CURRENT_SERVER_HANDLE,
                    Wtsapi32.WTS_CURRENT_SESSION,
                    Wtsapi32.WTS_INFO_CLASS.WTSClientProtocolType,
                    out var buffer,
                    out var bytesReturned))
            {
                try
                {
                    if (bytesReturned >= 2)
                    {
                        var protocol = System.Runtime.InteropServices.Marshal.ReadInt16(buffer);
                        // 0 = console, 1 = ICA (Citrix), 2 = RDP
                        return protocol != 0;
                    }
                }
                finally
                {
                    Wtsapi32.WTSFreeMemory(buffer);
                }
            }
        }
        catch
        {
            // Fall through to false
        }

        return false;
    }

    /// <summary>
    /// Starts monitoring for session changes (lock/unlock, RDP connect/disconnect).
    /// </summary>
    public static void StartWatching()
    {
        if (_watching) return;
        _watching = true;
        _lastRemoteState = IsRemoteSession();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>
    /// Stops monitoring session changes.
    /// </summary>
    public static void StopWatching()
    {
        if (!_watching) return;
        _watching = false;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Check if session type changed after relevant events
        if (e.Reason is SessionSwitchReason.RemoteConnect
            or SessionSwitchReason.RemoteDisconnect
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.SessionLogon)
        {
            var isRemote = IsRemoteSession();
            if (isRemote != _lastRemoteState)
            {
                _lastRemoteState = isRemote;
                SessionChanged?.Invoke(null, new SessionChangedEventArgs(isRemote));
            }
        }
    }
}

public class SessionChangedEventArgs : EventArgs
{
    public bool IsRemoteSession { get; }

    public SessionChangedEventArgs(bool isRemoteSession)
    {
        IsRemoteSession = isRemoteSession;
    }
}
