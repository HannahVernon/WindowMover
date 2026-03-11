using System.Runtime.InteropServices;

namespace WindowMover.Core.Native;

/// <summary>
/// P/Invoke for WTS (Windows Terminal Services) session queries.
/// Used for RDP session detection.
/// </summary>
internal static partial class Wtsapi32
{
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQuerySessionInformationW(
        IntPtr hServer,
        int sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [LibraryImport("wtsapi32.dll")]
    public static partial void WTSFreeMemory(IntPtr pMemory);

    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
    public const int WTS_CURRENT_SESSION = -1;

    public enum WTS_INFO_CLASS
    {
        WTSClientProtocolType = 16
    }
}
