using System;
using Microsoft.UI.Dispatching;          // DispatcherQueue

// Snapshot the UI renders: whether monitoring is on, the latest result, and the probe schedule.
record WatchdogState(bool Monitoring, NetState Net, DateTimeOffset? LastProbeAt, DateTimeOffset? NextProbeAt)
{
    public static readonly WatchdogState Off = new(false, new NetState(NetStatus.Unknown, "Not monitoring"), null, null);
}

// App-lifetime connectivity state shared by the tray icon and the popout (which subscribes through
// UseExternalStore), so it outlives the popout window.
//
// This is traydog's one piece of work while hidden, and the documented exception to "no network
// work while hidden": the tray icon *is* the status display. It's opt-in (the user starts it, and
// the choice is remembered across launches), it runs one tiny probe at a time, and it slows to
// InTrayInterval (a setting, a minute by default) unless the status page is on screen.
static class Watchdog
{
    public static readonly TimeSpan WhileOpenInterval = TimeSpan.FromSeconds(15);

    static DispatcherQueue? _dispatcher;
    static ConnectivityMonitor? _monitor;
    static bool _statusOnScreen;

    public static WatchdogState State { get; private set; } = WatchdogState.Off;

    public static TimeSpan InTrayInterval { get; private set; } = CheckInterval.DefaultInTray;

    // Raised on the UI thread whenever State changes.
    public static event Action? Changed;

    // UseExternalStore's subscribe shape: returns the unsubscribe action.
    public static Action Subscribe(Action onChange)
    {
        Changed += onChange;
        return () => Changed -= onChange;
    }

    // Call once on the UI thread at startup; resumes monitoring if the user left it on.
    public static void Start()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        InTrayInterval = CheckInterval.SanitizeInTray(SettingsService.InTrayIntervalSeconds);
        if (SettingsService.MonitoringEnabled)
            SetMonitoring(true);
    }

    public static void SetInTrayInterval(TimeSpan interval)
    {
        InTrayInterval = interval;
        SettingsService.InTrayIntervalSeconds = (int)interval.TotalSeconds;
        if (_monitor is not null)
            _monitor.Interval = CurrentInterval;
    }

    public static void SetMonitoring(bool on)
    {
        SettingsService.MonitoringEnabled = on;
        if (on == (_monitor is not null))
            return;

        _monitor?.Dispose();
        _monitor = null;
        if (!on)
        {
            Publish(WatchdogState.Off);
            return;
        }

        var monitor = new ConnectivityMonitor(CurrentInterval);
        monitor.StatusChanged += (status, detail) =>
            Post(monitor, () => Publish(State with { Net = new NetState(status, detail) }));
        monitor.Scheduled += (last, next) =>
            Post(monitor, () => Publish(State with { LastProbeAt = last, NextProbeAt = next }));
        _monitor = monitor;

        Publish(new WatchdogState(true, new NetState(NetStatus.Unknown, "Checking…"), null, null));
        monitor.Start();
    }

    // Told when the status page comes on screen or leaves it: probes speed up while someone is
    // looking, and a stale result refreshes right away.
    public static void SetStatusOnScreen(bool onScreen)
    {
        if (onScreen == _statusOnScreen)
            return;

        _statusOnScreen = onScreen;
        if (_monitor is null)
            return;

        _monitor.Interval = CurrentInterval;
        if (onScreen && State.LastProbeAt is { } last && DateTimeOffset.UtcNow - last > WhileOpenInterval / 2)
            _monitor.ProbeNow();
    }

    // App exit: stop probing without recording "off" as the user's choice.
    public static void Stop()
    {
        _monitor?.Dispose();
        _monitor = null;
    }

    static TimeSpan CurrentInterval => _statusOnScreen ? WhileOpenInterval : InTrayInterval;

    // Monitor events arrive on background threads; drop any from a monitor that's been replaced.
    static void Post(ConnectivityMonitor source, Action update) =>
        _dispatcher?.TryEnqueue(() =>
        {
            if (ReferenceEquals(source, _monitor))
                update();
        });

    static void Publish(WatchdogState next)
    {
        if (next == State)
            return;

        State = next;
        Changed?.Invoke();
    }
}
