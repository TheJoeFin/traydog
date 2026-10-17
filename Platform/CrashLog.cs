using System;
using System.IO;

// Last-resort record of an unhandled exception, appended to %TEMP%\traydog-crash.log. A tray app
// has no window to show an error in, so without this a crash just makes the icon vanish.
static class CrashLog
{
    public static void Write(Exception? ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "traydog-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }
}
