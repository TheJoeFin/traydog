// Internet monitoring as a monitor situations can drive. Its state and probing live in Watchdog.
sealed class InternetMonitor : IMonitor
{
    public const string MonitorId = "internet";

    public static readonly InternetMonitor Instance = new();

    InternetMonitor() { }

    public string Id => MonitorId;

    public string Name => "Internet monitoring";

    public string Glyph => ""; // Wifi

    public void SetRunning(bool running) => Watchdog.SetMonitoring(running);
}
