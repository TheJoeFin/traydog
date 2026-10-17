using System;

// Words, icons, and countdown math for each monitoring state, shared by the popout and the tray
// icon. No WinRT here, so the test project links this file directly.
static class StatusText
{
    public const string AppName = "traydog";
    const int MaxTooltipLength = 127; // NOTIFYICONDATA.szTip

    // Popout headline for the current monitoring/connection state.
    public static string Headline(bool monitoring, NetStatus s) =>
        !monitoring ? "Monitoring paused" :
        s switch
        {
            NetStatus.Online => "✅ You're online",
            NetStatus.NoInternet => "⚠️ Internet unavailable",
            NetStatus.Offline => "🚫 No network connection",
            _ => "🔄️ Checking connection"
        };

    public static string Description(bool monitoring, NetState net) =>
        !monitoring
            ? "Start monitoring to see your connection health here."
            : net.Detail;

    // Caption under the monitoring button.
    public static string Cadence(bool monitoring, TimeSpan whileOpen, TimeSpan inTray) =>
        monitoring
            ? $"Checking every {CheckInterval.Describe(whileOpen)} while open, every {CheckInterval.Describe(inTray)} in the tray"
            : "Start monitoring to keep an eye on your connection";

    // Second tooltip line under the app name.
    public static string TrayStatus(bool monitoring, NetStatus s) =>
        !monitoring ? "Monitoring off" :
        s switch
        {
            NetStatus.Online => "Connected",
            NetStatus.NoInternet => "No internet (network up)",
            NetStatus.Offline => "Offline",
            _ => "Checking…"
        };

    public static string Tooltip(bool monitoring, NetStatus s)
    {
        string tooltip = $"{AppName}\n{TrayStatus(monitoring, s)}";
        return tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;
    }

    // Status -> tray icon filename under Assets\. These are full-color emoji, so they read on light
    // and dark taskbars alike and need no light-theme variants.
    public static string IconFile(bool monitoring, NetStatus s) =>
        !monitoring ? "trayicon.ico" :
        s switch
        {
            NetStatus.Online => "Check mark button.ico",
            NetStatus.NoInternet => "Warning.ico",
            NetStatus.Offline => "Globe with meridians.ico",
            _ => "trayicon.ico" // still probing
        };

    // Countdown bar value (0-100) between the last probe and the next. Null when no schedule is
    // known yet or the next probe is due (a probe is running), which the bar shows as indeterminate.
    public static double? ProgressPercent(DateTimeOffset? lastProbeAt, DateTimeOffset? nextProbeAt, DateTimeOffset now)
    {
        if (lastProbeAt is not { } last || nextProbeAt is not { } next || now >= next)
            return null;

        double total = (next - last).TotalSeconds;
        return total <= 0 ? null : Math.Clamp((now - last).TotalSeconds / total * 100, 0, 100);
    }
}
