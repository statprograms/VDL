using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Common.Utils.VDL.OS;

internal static class Win32Helpers
{
    private readonly static ThreadLocal<StringBuilder> _titleBuffer = new(() => new StringBuilder(512));
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public enum SystemErrorCodes
    {
        ERROR_ACCESS_DENIED = 0x5,
        ERROR_INVALID_HANDLE = 0x6
    }

    public static string GetWindowTitle(IntPtr hWnd)
    {
        var buffer = _titleBuffer.Value!;
        buffer.Clear();
        return GetWindowText(hWnd, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    public static bool IsMainWindow(IntPtr hWnd)
    {
        if (!IsWindow(hWnd))
        {
            return false;
        }

        long style = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
        return IsMainWindow(hWnd, style);
    }

    public static bool IsMainWindow(IntPtr hWnd, long style)
    {
        // hidden windows or child controls are never main windows
        if ((style & WS_VISIBLE) == 0
         || (style & WS_CHILD) != 0)
        {
            return false;
        }

        // owned windows (like dialogs) are not main windows
        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        IntPtr parent = GetAncestor(hWnd, GA_PARENT);
        IntPtr desktop = GetDesktopWindow();

        return parent == desktop || parent == IntPtr.Zero;
    }

    public static bool IsPopup(long style)
        =>
        (style & WS_POPUP) != 0;

    public static bool HasSystemMenu(long style)
        =>
        (style & WS_SYSMENU) != 0;

    public static void Maximize(IntPtr hWnd)
        =>
        ShowWindowAsync(hWnd, nCmdShow: SW_MAXIMIZE);

    // Maximizes a window without the system jumping to its desktop
    public static void MaximizeNoActivate(IntPtr hWnd)
    {
        // get the primary screen dimensions
        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);

        // resize and move the window to (0,0)
        // using SWP_NOACTIVATE prevents the OS from switching desktops
        SetWindowPos(
            hWnd,
            IntPtr.Zero,      // ignored because of SWP_NOZORDER
            0, 0,             // X, Y coordinates
            screenWidth,      // new Width
            screenHeight,     // new Height
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOZORDER
        );
    }

    public static void Minimize(IntPtr hWnd)
        =>
        ShowWindowAsync(hWnd, SW_MINIMIZE);

    public static bool IsWindowCloaked(IntPtr hWnd)
    {
        DwmGetWindowAttribute(hWnd, (int)DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, out int cloaked, sizeof(int));
        return cloaked != 0;
    }

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : GetWindowLong32(hWnd, nIndex);
    }

    public static string GetClassNameStr(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

    public static IntPtr GetInvokingTerminalWindow()
    {
        var foreground = GetForegroundWindow();
        var console = GetConsoleWindow();
        return ResolveInvokingTerminalWindow(foreground, GetClassNameStr(foreground),
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")),
            console != IntPtr.Zero && !IsWindowVisible(console));
    }

    internal static IntPtr ResolveInvokingTerminalWindow(IntPtr foreground, string windowClass,
        bool windowsTerminalSession, bool hiddenConsole)
        => (windowsTerminalSession || hiddenConsole) && windowClass == "CASCADIA_HOSTING_WINDOW_CLASS"
            ? foreground : IntPtr.Zero;

    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();

    // -- WinAPI Interop --

    private enum DWMWINDOWATTRIBUTE : uint
    {
        DWMWA_CLOAKED = 14
    }

    private const uint GA_PARENT = 1;
    public const uint GW_OWNER = 4;
    public const int GWL_STYLE = -16;
    public const long WS_VISIBLE = 0x10000000L;
    public const long WS_CHILD = 0x40000000L;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_SYSMENU = 0x00080000;

    const int SW_MAXIMIZE = 3;
    const int SW_MINIMIZE = 6;

    const uint SWP_NOACTIVATE = 0x0010; // Does not activate the window
    const uint SWP_SHOWWINDOW = 0x0040; // Displays the window
    const uint SWP_NOZORDER = 0x0004; // Retains current Z order (ignores hWndInsertAfter)

    // System Metrics Indexes
    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc lp, IntPtr lp2);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, uint a, out int p, int s);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
