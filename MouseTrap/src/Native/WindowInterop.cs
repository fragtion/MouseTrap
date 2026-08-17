using System.Runtime.InteropServices;


namespace MouseTrap.Native;

/// <summary>
/// Win32 helpers for inspecting and (carefully) moving <b>foreign</b> top level windows.
/// <para>
/// Everything in here assumes the process is manifested as <c>PerMonitorV2</c> (see app.manifest),
/// so all coordinates returned/accepted are true physical pixels on the virtual desktop.
/// If the manifest ever loses PerMonitorV2, Win32 starts virtualizing these coordinates per
/// DPI-awareness of the caller and every calculation below silently becomes wrong.
/// </para>
/// </summary>
internal static class WindowInterop {
    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32.RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>Windows 10 1607+. Returns 96 for unaware windows.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Cross process message send that can never wedge the caller.
    /// Never use plain SendMessage from the polling loop: if the target UI thread is busy
    /// (or itself blocked on us) the loop deadlocks and the cursor clip is never released.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT {
        public int length;
        public int flags;
        public int showCmd;
        public Point ptMinPosition;
        public Point ptMaxPosition;
        public Win32.RECT rcNormalPosition;
    }

    #endregion

    #region constants

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    private const long WS_CHILD = 0x40000000L;
    private const long WS_MINIMIZE = 0x20000000L;
    private const long WS_MAXIMIZE = 0x01000000L;
    private const long WS_DISABLED = 0x08000000L;

    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;

    private const uint GA_ROOT = 2;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;

    public const int WM_NCHITTEST = 0x0084;
    public const int HTVSCROLL = 7;
    public const int HTHSCROLL = 6;

    private const uint SMTO_ABORTIFHUNG = 0x0002;

    #endregion


    /// <summary>DPI awareness of a *window* (which is really the awareness of its owning process/thread).</summary>
    public enum DpiAwareness {
        Invalid = -1,
        Unaware = 0,
        SystemAware = 1,
        PerMonitorAware = 2,
    }


    private static long GetStyle(IntPtr hWnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, index).ToInt64()
            : GetWindowLong32(hWnd, index);
    }


    public static IntPtr RootOf(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) {
            return IntPtr.Zero;
        }

        var root = GetAncestor(hWnd, GA_ROOT);
        return root == IntPtr.Zero ? hWnd : root;
    }


    public static IntPtr FromPoint(Point pt)
    {
        return WindowFromPoint(pt);
    }


    public static bool TryGetBounds(IntPtr hWnd, out Rectangle bounds)
    {
        if (hWnd != IntPtr.Zero && GetWindowRect(hWnd, out var rect)) {
            bounds = rect;
            return bounds.Width > 0 && bounds.Height > 0;
        }

        bounds = Rectangle.Empty;
        return false;
    }


    public static string ClassNameOf(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) {
            return string.Empty;
        }

        var buffer = new char[256];
        var len = GetClassName(hWnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }


    public static uint ProcessIdOf(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        return pid;
    }


    /// <summary>
    /// Whether it is safe for us to reposition this window from the outside.
    /// <para>
    /// This deliberately rejects everything the old implementation happily moved:
    /// child controls (moving a child with screen coordinates puts it outside its parent's
    /// client area, which is exactly what produced the "window turns black" reports),
    /// minimized/maximized windows, the shell/desktop, and our own process.
    /// </para>
    /// </summary>
    public static bool IsSafeToMove(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || !IsWindowVisible(hWnd)) {
            return false;
        }

        // must be a real top level window, never a child control
        if (RootOf(hWnd) != hWnd) {
            return false;
        }

        var style = GetStyle(hWnd, GWL_STYLE);
        if ((style & WS_CHILD) != 0 || (style & WS_MINIMIZE) != 0 || (style & WS_DISABLED) != 0) {
            return false;
        }

        // A maximized (or snapped) window must not be moved with SetWindowPos: the drag loop owns
        // its restore geometry and forcing a rect on it leaves DWM with a stale redirection surface.
        if ((style & WS_MAXIMIZE) != 0 || IsMaximized(hWnd)) {
            return false;
        }

        var ex = GetStyle(hWnd, GWL_EXSTYLE);
        if ((ex & WS_EX_NOACTIVATE) != 0 && (ex & WS_EX_TOOLWINDOW) != 0) {
            return false;
        }

        // never touch the shell
        switch (ClassNameOf(hWnd)) {
            case "Progman":
            case "WorkerW":
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
            case "Windows.UI.Core.CoreWindow":
            case "ForegroundStaging":
                return false;
        }

        return ProcessIdOf(hWnd) != (uint) Environment.ProcessId;
    }


    public static bool IsMaximized(IntPtr hWnd)
    {
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hWnd, ref placement)) {
            return false;
        }

        return placement.showCmd is SW_SHOWMAXIMIZED or SW_SHOWMINIMIZED;
    }


    public static DpiAwareness AwarenessOf(IntPtr hWnd)
    {
        try {
            var ctx = GetWindowDpiAwarenessContext(hWnd);
            return ctx == IntPtr.Zero
                ? DpiAwareness.Invalid
                : (DpiAwareness) GetAwarenessFromDpiAwarenessContext(ctx);
        }
        catch (EntryPointNotFoundException) {
            return DpiAwareness.Invalid;
        }
    }


    public static uint DpiOf(IntPtr hWnd)
    {
        try {
            var dpi = GetDpiForWindow(hWnd);
            return dpi == 0 ? 96 : dpi;
        }
        catch (EntryPointNotFoundException) {
            return 96;
        }
    }


    public static uint DpiOfMonitorAt(Point pt)
    {
        try {
            var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX != 0) {
                return dpiX;
            }
        }
        catch (DllNotFoundException) {
            // pre Windows 8.1
        }
        catch (EntryPointNotFoundException) {
        }

        return 96;
    }


    /// <summary>
    /// Move a foreign top level window.
    /// <para>
    /// Position only by default. We deliberately do <b>not</b> force a new size when the window
    /// crosses a DPI boundary: Windows itself sends <c>WM_DPICHANGED</c> (per-monitor aware apps
    /// resize themselves from the suggested rect) or rescales the window for unaware apps.
    /// Pushing our own size in on top of that is what desynchronises the DWM redirection surface
    /// and leaves the window painted black.
    /// </para>
    /// </summary>
    /// <param name="size">Optional explicit size. Only pass this for windows whose awareness makes
    /// Windows keep the physical size across a DPI change and you have computed the ratio yourself.</param>
    public static bool Move(IntPtr hWnd, int x, int y, Size? size = null)
    {
        var flags = SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;
        var cx = 0;
        var cy = 0;

        if (size is { Width: > 0, Height: > 0 } s) {
            cx = s.Width;
            cy = s.Height;
        }
        else {
            flags |= SWP_NOSIZE;
        }

        return SetWindowPos(hWnd, IntPtr.Zero, x, y, cx, cy, flags);
    }


    public static bool Resize(IntPtr hWnd, Size size)
    {
        return SetWindowPos(hWnd, IntPtr.Zero, 0, 0, size.Width, size.Height,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }


    /// <summary>Non blocking WM_NCHITTEST. Returns <c>null</c> when the target did not answer in time.</summary>
    public static int? HitTest(IntPtr hWnd, Point screenPoint)
    {
        var lParam = (IntPtr) ((screenPoint.Y << 16) | (screenPoint.X & 0xFFFF));
        var ok = SendMessageTimeout(hWnd, WM_NCHITTEST, IntPtr.Zero, lParam, SMTO_ABORTIFHUNG, 30, out var result);
        return ok == IntPtr.Zero ? null : (int) result;
    }
}
