using System;
using Windows.Graphics;                  // PointInt32, SizeInt32, RectInt32

// Pure geometry for positioning the popout: clamps it inside the target monitor's work area
// and flips it to whichever side of the cursor or taskbar has room.
static class PopoutPlacement
{
    const int Margin = 8;

    public static PointInt32 Calculate(PointInt32 cursor, SizeInt32 popupSize, RectInt32 workArea)
    {
        int maxX = Math.Max(workArea.X, workArea.X + workArea.Width - popupSize.Width);
        int maxY = Math.Max(workArea.Y, workArea.Y + workArea.Height - popupSize.Height);

        int x = Math.Clamp(cursor.X, workArea.X, maxX);
        int above = cursor.Y - popupSize.Height - Margin;
        int below = cursor.Y + Margin;

        int y = above >= workArea.Y
            ? Math.Min(above, maxY)
            : below <= maxY
                ? Math.Max(below, workArea.Y)
                : Math.Clamp(above, workArea.Y, maxY);

        return new PointInt32(x, y);
    }

    public static PointInt32 CalculateForTaskbar(
        RectInt32 taskbar,
        SizeInt32 popupSize,
        RectInt32 workArea)
    {
        int maxX = Math.Max(workArea.X, workArea.X + workArea.Width - popupSize.Width);
        int maxY = Math.Max(workArea.Y, workArea.Y + workArea.Height - popupSize.Height);
        bool isHorizontal = taskbar.Width >= taskbar.Height;

        if (isHorizontal)
        {
            int horizontalX = Math.Clamp(
                taskbar.X + taskbar.Width / 2 - popupSize.Width / 2,
                workArea.X,
                maxX);
            bool isTop = taskbar.Y < workArea.Y + workArea.Height / 2;
            int horizontalY = isTop
                ? taskbar.Y + taskbar.Height + Margin
                : taskbar.Y - popupSize.Height - Margin;
            return new PointInt32(horizontalX, Math.Clamp(horizontalY, workArea.Y, maxY));
        }

        int verticalY = Math.Clamp(
            taskbar.Y + taskbar.Height / 2 - popupSize.Height / 2,
            workArea.Y,
            maxY);
        bool isLeft = taskbar.X < workArea.X + workArea.Width / 2;
        int verticalX = isLeft
            ? taskbar.X + taskbar.Width + Margin
            : taskbar.X - popupSize.Width - Margin;
        return new PointInt32(Math.Clamp(verticalX, workArea.X, maxX), verticalY);
    }
}
