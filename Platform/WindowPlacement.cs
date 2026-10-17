using System.Diagnostics.CodeAnalysis;
using System.Drawing;                    // Point, Size, Rectangle
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;            // AppWindow, DisplayArea
using Windows.Graphics;                  // PointInt32, RectInt32
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Shell;
using WinUIEx;                           // TrayIcon

// Places the popout next to the tray icon: anchored on the icon's rect from
// Shell_NotifyIconGetRect, then on the cursor (or the notification area when the popout wasn't
// opened by a click), pushed against the taskbar edge, at the anchor monitor's DPI.
static class WindowPlacement
{
    const uint ABM_GETTASKBARPOS = 0x5;
    const int WindowMargin = 12;

    static nint _trayIconWindowHandle;
    static uint _trayIconId;

    // Captures the hidden message window WinUIEx uses for the tray icon so the icon rect can be
    // queried. WinUIEx doesn't expose it, so this reads a private field and silently degrades to
    // cursor placement if the internals change.
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, typeof(TrayIcon))]
    public static void SetTrayIcon(TrayIcon trayIcon)
    {
        _trayIconId = trayIcon.TrayIconId;
        try
        {
            var field = typeof(TrayIcon).GetField("_windowHandle", BindingFlags.Instance | BindingFlags.NonPublic);
            _trayIconWindowHandle = field?.GetValue(trayIcon) is nint handle ? handle : 0;
        }
        catch
        {
            _trayIconWindowHandle = 0;
        }
    }

    // Moves and resizes the window (logical size) next to the tray icon.
    public static void PositionNearTray(AppWindow window, int width, int height, bool fromTrayClick)
    {
        Point anchor = GetAnchor(fromTrayClick);
        RectInt32 work = DisplayArea.GetFromPoint(new PointInt32(anchor.X, anchor.Y), DisplayAreaFallback.Primary).WorkArea;
        uint dpi = GetDpiForPoint(anchor);
        int margin = PopoutPlacement.ToPhysical(WindowMargin, dpi);
        var size = PopoutPlacement.FitToWorkArea(
            new Size(PopoutPlacement.ToPhysical(width, dpi), PopoutPlacement.ToPhysical(height, dpi)),
            new Size(work.Width, work.Height), margin);

        bool haveTaskbar = TryGetTaskbar(out Rectangle taskbar, out TaskbarEdge edge);
        Point position = PopoutPlacement.Calculate(
            anchor,
            size,
            new Rectangle(work.X, work.Y, work.Width, work.Height),
            haveTaskbar ? taskbar : null,
            edge,
            margin);

        window.MoveAndResize(new RectInt32(position.X, position.Y, size.Width, size.Height));
    }

    static Point GetAnchor(bool fromTrayClick)
    {
        if (TryGetTrayIconRect(out RECT icon))
            return new Point((icon.left + icon.right) / 2, (icon.top + icon.bottom) / 2);

        // A click in the overflow flyout reports an empty icon rect; the cursor is right there.
        // A launch (first run, second instance) has no meaningful cursor, so use the tray instead.
        if (!fromTrayClick && TryGetNotificationAreaRect(out RECT tray))
            return new Point((tray.left + tray.right) / 2, (tray.top + tray.bottom) / 2);

        PInvoke.GetCursorPos(out Point cursor);
        return cursor;
    }

    static bool TryGetTrayIconRect(out RECT rect)
    {
        rect = default;
        if (_trayIconWindowHandle == 0)
            return false;

        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = (HWND)_trayIconWindowHandle,
            uID = _trayIconId,
        };
        // Icons hidden in the overflow area report an empty rect.
        return PInvoke.Shell_NotifyIconGetRect(in id, out rect).Succeeded && rect.right > rect.left;
    }

    static bool TryGetNotificationAreaRect(out RECT rect)
    {
        rect = default;
        HWND taskbar = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (taskbar == HWND.Null)
            return false;

        HWND notificationArea = PInvoke.FindWindowEx(taskbar, HWND.Null, "TrayNotifyWnd", null);
        return notificationArea != HWND.Null && PInvoke.GetWindowRect(notificationArea, out rect);
    }

    static bool TryGetTaskbar(out Rectangle rect, out TaskbarEdge edge)
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (PInvoke.SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == 0)
        {
            rect = default;
            edge = TaskbarEdge.Bottom;
            return false;
        }

        rect = Rectangle.FromLTRB(data.rc.left, data.rc.top, data.rc.right, data.rc.bottom);
        edge = (TaskbarEdge)data.uEdge;
        return true;
    }

    static uint GetDpiForPoint(Point point)
    {
        HMONITOR monitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded ? dpiX : 96;
    }
}
