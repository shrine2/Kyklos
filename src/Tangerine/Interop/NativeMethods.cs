using System.Runtime.InteropServices;

namespace Tangerine.Interop;

/// <summary>Win32 entry points used by the layered radial wheel, the drop surface and the global hotkey.</summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------ window styles
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int GWL_EXSTYLE = -20;

    public const int SW_SHOWNOACTIVATE = 4;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // ------------------------------------------------------------------ layered windows
    public const int ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ------------------------------------------------------------------ input
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

    public const uint GA_PARENT = 1;
    public const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;      // Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_ESCAPE = 0x1B;

    // ------------------------------------------------------------------ inter-process
    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    public const int WM_COPYDATA = 0x004A;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);

    // ------------------------------------------------------------------ DPI
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Effective DPI scale (1.0 = 96 DPI) of the monitor containing a screen point.</summary>
    public static float ScaleForPoint(Point screenPoint)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT(screenPoint.X, screenPoint.Y), MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96f;
        }
        catch
        {
            // shcore unavailable — assume 100%
        }
        return 1f;
    }

    public static Point CursorPosition()
        => GetCursorPos(out var point) ? new Point(point.X, point.Y) : Point.Empty;

    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    // ------------------------------------------------------------------ shell hit testing

    /// <summary>Window classes that make up Explorer's file views and the desktop.</summary>
    private static readonly string[] ShellSurfaceClasses =
    [
        "SHELLDLL_DefView", "Progman", "WorkerW", "CabinetWClass", "ExploreWClass",
    ];

    /// <summary>
    /// True when the window under the point belongs to an Explorer file view or the desktop.
    /// The drag trigger uses this so the drop surface only ever arms for a file drag,
    /// never for a Ctrl+Alt drag inside an unrelated application.
    /// </summary>
    public static bool IsOverShellSurface(Point screenPoint)
    {
        try
        {
            var handle = WindowFromPoint(new POINT(screenPoint.X, screenPoint.Y));
            for (var depth = 0; depth < 8 && handle != IntPtr.Zero; depth++)
            {
                var name = ClassNameOf(handle);
                if (name.Length > 0 && ShellSurfaceClasses.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return true;

                handle = GetAncestor(handle, GA_PARENT);
            }
        }
        catch
        {
            // Treat unknown windows as non-shell: safer to not arm.
        }
        return false;
    }

    private static string ClassNameOf(IntPtr handle)
    {
        var buffer = new System.Text.StringBuilder(256);
        var length = GetClassName(handle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    // ---- process teardown -------------------------------------------------

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    /// <summary>
    /// Ends the process without running DLL detach or object finalisers. Some GPU drivers
    /// — observed with atidxx64.dll on AMD — fault during orderly teardown after a mix of
    /// WinRT, WIC and GDI+ work, which turns a clean shutdown into an access violation.
    /// Everything we produce is already flushed to disk when this is called, so skipping
    /// detach is safe and keeps the exit code meaningful.
    /// </summary>
    public static void HardExit(int exitCode)
    {
        try { Console.Out.Flush(); } catch { /* console may be detached */ }
        TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode));
        Environment.Exit(exitCode); // only reached if the call above fails
    }
}
