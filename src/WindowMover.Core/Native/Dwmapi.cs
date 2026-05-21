using System.Runtime.InteropServices;

namespace WindowMover.Core.Native;

/// <summary>
/// P/Invoke declarations for dwmapi.dll — Desktop Window Manager queries.
/// </summary>
internal static partial class Dwmapi
{
    public const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// Retrieves the value of a DWM window attribute.
    /// Used to detect cloaked (hidden) windows that report WS_VISIBLE but are not
    /// actually rendered on screen (e.g., suspended UWP apps like Settings).
    /// </summary>
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        out int pvAttribute,
        int cbAttribute);

    /// <summary>
    /// Returns true if the window is cloaked (hidden by DWM despite having WS_VISIBLE).
    /// </summary>
    public static bool IsWindowCloaked(IntPtr hWnd)
    {
        int hr = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }
}
