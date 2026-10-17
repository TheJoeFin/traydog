using Windows.Storage;

// Typed wrapper over ApplicationData.LocalSettings. Every read falls back to a default so a missing
// or corrupt value (or no package identity, e.g. the --devtools profile) never crashes the app.
// LocalSettings values must be small WinRT primitives; nothing secret belongs here.
static class SettingsService
{
    // False until the first launch has shown the popout, so starting with Windows stays quiet.
    public static bool HasLaunchedBefore
    {
        get => Get(nameof(HasLaunchedBefore), false);
        set => Set(nameof(HasLaunchedBefore), value);
    }

    // Whether the user left a monitor on (its usual choice when no situation decides); resumed at the
    // next launch. Internet monitoring predates the other monitors and keeps its original key.
    public static bool GetMonitorOn(string monitorId) => Get(MonitorKey(monitorId), false);

    public static void SetMonitorOn(string monitorId, bool on) => Set(MonitorKey(monitorId), on);

    static string MonitorKey(string monitorId) =>
        monitorId == InternetMonitor.MonitorId ? "MonitoringEnabled" : $"Monitor.{monitorId}.On";

    // Seconds between checks while the popout is closed. Raw value; Watchdog snaps it to
    // CheckInterval.InTrayChoices on read.
    public static int InTrayIntervalSeconds
    {
        get => Get(nameof(InTrayIntervalSeconds), (int)CheckInterval.DefaultInTray.TotalSeconds);
        set => Set(nameof(InTrayIntervalSeconds), value);
    }

    static T Get<T>(string key, T defaultValue)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) && value is T typed
                ? typed
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    static void Set(string key, object value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
        catch
        {
            // Settings are best-effort; ignore storage failures.
        }
    }
}
