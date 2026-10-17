using System;
using System.Drawing;                    // Point, Size, Rectangle: plain structs, so this file stays testable on net10.0

// Which screen edge the taskbar is docked to. Values match the Win32 ABE_* constants.
enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3
}

// Pure geometry for positioning the popout: centered on the anchor (the tray icon, or the cursor
// when the icon's rect is unknown), pushed against the taskbar edge, and clamped inside the
// monitor's work area with a margin. All values are physical pixels.
static class PopoutPlacement
{
    public static Point Calculate(
        Point anchor,
        Size popup,
        Rectangle workArea,
        Rectangle? taskbar,
        TaskbarEdge edge,
        int margin)
    {
        int x = anchor.X - popup.Width / 2;
        int y = workArea.Bottom - popup.Height - margin;

        if (taskbar is { } bar)
        {
            switch (edge)
            {
                case TaskbarEdge.Bottom:
                    y = Math.Min(bar.Top, workArea.Bottom) - popup.Height - margin;
                    break;
                case TaskbarEdge.Top:
                    y = Math.Max(bar.Bottom, workArea.Top) + margin;
                    break;
                case TaskbarEdge.Left:
                    x = Math.Max(bar.Right, workArea.Left) + margin;
                    y = anchor.Y - popup.Height + margin;
                    break;
                case TaskbarEdge.Right:
                    x = Math.Min(bar.Left, workArea.Right) - popup.Width - margin;
                    y = anchor.Y - popup.Height + margin;
                    break;
            }
        }

        int maxX = Math.Max(workArea.Left + margin, workArea.Right - popup.Width - margin);
        int maxY = Math.Max(workArea.Top + margin, workArea.Bottom - popup.Height - margin);
        return new Point(
            Math.Clamp(x, workArea.Left + margin, maxX),
            Math.Clamp(y, workArea.Top + margin, maxY));
    }

    // Logical (96-DPI) pixels to physical pixels at the given DPI, rounding up so content never clips.
    public static int ToPhysical(int logical, uint dpi) => (int)Math.Ceiling(logical * (dpi == 0 ? 96 : dpi) / 96.0);
}
