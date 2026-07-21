using System;
using System.Runtime.InteropServices;
using Windows.Graphics;                  // RectInt32

// Thin Win32 / DWM interop surface used to place the popout relative to the taskbar and
// notification area, read the cursor position, and round the window corners.
static class Interop
{
    const uint DwmwaWindowCornerPreference = 33;
    const uint DwmwcpRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    public static void ApplyRoundedCorners(nint windowHandle)
    {
        uint preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmwaWindowCornerPreference,
            ref preference,
            sizeof(uint));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(
        nint windowHandle,
        uint attribute,
        ref uint value,
        uint valueSize);

    public static bool TryGetNotificationAreaBounds(out RectInt32 bounds)
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var notificationArea = taskbar == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);

        return TryGetBounds(notificationArea, out bounds);
    }

    public static bool TryGetTaskbarBounds(out RectInt32 bounds) =>
        TryGetBounds(FindWindow("Shell_TrayWnd", null), out bounds);

    static bool TryGetBounds(nint windowHandle, out RectInt32 bounds)
    {
        if (windowHandle != IntPtr.Zero && GetWindowRect(windowHandle, out var rect))
        {
            bounds = new RectInt32(
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top);
            return true;
        }

        bounds = default;
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindowEx(
        nint parentWindow,
        nint childAfter,
        string className,
        string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(nint windowHandle, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
