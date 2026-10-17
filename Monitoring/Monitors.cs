using System;
using System.Collections.Generic;
using System.Linq;

// Something traydog runs in the background that the user, and situations, can switch on and off.
// Internet monitoring (InternetMonitor) is the first. A new monitor implements this and is registered
// in TrayApp.Start; situations can then drive it, and the editor lists it, with no other changes.
interface IMonitor
{
    // Stable: situations.json refers to monitors by it.
    string Id { get; }

    // "Internet monitoring", shown in the situation editor and the tray menu.
    string Name { get; }

    // Segoe Fluent Icons glyph.
    string Glyph { get; }

    // Start or stop the work. Called on the UI thread, only when the answer changes.
    void SetRunning(bool running);
}

// App-lifetime switchboard for the registered monitors: each one's MonitorSwitch (the user's usual
// choice, what situations demand, any override) and the resulting on/off. The user's controls call
// Choose; SituationEngine calls ApplyDemands. UI thread only.
static class Monitors
{
    static readonly List<IMonitor> Registered = [];
    static readonly Dictionary<string, MonitorSwitch> Switches = [];

    public static IReadOnlyList<IMonitor> All => Registered;

    // Raised on the UI thread whenever a switch changes.
    public static event Action? Changed;

    // UseExternalStore's subscribe shape: returns the unsubscribe action.
    public static Action Subscribe(Action onChange)
    {
        Changed += onChange;
        return () => Changed -= onChange;
    }

    public static MonitorSwitch Switch(string id) =>
        Switches.TryGetValue(id, out var monitorSwitch) ? monitorSwitch : new MonitorSwitch(false, null, null);

    public static bool IsOn(string id) => Switch(id).IsOn;

    // Adds a monitor and starts it if the user left it on. Situations take over at their first evaluation.
    public static void Register(IMonitor monitor)
    {
        if (Registered.Any(m => m.Id == monitor.Id))
            return;

        Registered.Add(monitor);
        var monitorSwitch = new MonitorSwitch(SettingsService.GetMonitorOn(monitor.Id), null, null);
        Switches[monitor.Id] = monitorSwitch;
        if (monitorSwitch.IsOn)
            monitor.SetRunning(true);
        Changed?.Invoke();
    }

    // The user switched a monitor (popout button, tray menu).
    public static void Choose(string id, bool on)
    {
        var current = Switch(id);
        // A situation in charge may want the monitor put back after a while.
        DateTime? expiresAt = current.Demand is { } demand &&
                              SituationEngine.State.Find(demand.SituationId)?.ReassertAfter is { } after
            ? DateTime.Now + after
            : null;
        var next = current.Choose(on, expiresAt);
        if (next.Usual != current.Usual)
            SettingsService.SetMonitorOn(id, next.Usual);
        Apply(id, next);
    }

    public static void Toggle(string id) => Choose(id, !IsOn(id));

    // Situations re-evaluated: each monitor gets its demand, or none.
    public static void ApplyDemands(IReadOnlyDictionary<string, SituationDemand> demands)
    {
        var now = DateTime.Now;
        foreach (var monitor in Registered)
            Apply(monitor.Id, Switch(monitor.Id).WithDemand(demands.TryGetValue(monitor.Id, out var d) ? d : null, now));
    }

    // When the earliest user override runs out and a situation should take over again; null if none will.
    public static DateTime? NextOverrideExpiry() =>
        Switches.Values.Select(s => s.Override?.Expires).Where(e => e is not null).Min();

    // App exit: stop everything without recording "off" as anyone's choice.
    public static void Stop()
    {
        foreach (var monitor in Registered.Where(m => IsOn(m.Id)))
            monitor.SetRunning(false);
    }

    static void Apply(string id, MonitorSwitch next)
    {
        var current = Switch(id);
        if (next == current)
            return;

        Switches[id] = next;
        if (next.IsOn != current.IsOn && Registered.FirstOrDefault(m => m.Id == id) is { } monitor)
            monitor.SetRunning(next.IsOn);
        Changed?.Invoke();
    }
}
