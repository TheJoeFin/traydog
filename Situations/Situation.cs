using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

// A named set of conditions about the PC's surroundings (network, place, power, devices, time) that,
// while it holds, switches monitors on or off. No WinRT here, so the test project links this file.
sealed record Situation(
    string Id,
    string Name,
    bool Enabled,
    SituationMatch Match,
    IReadOnlyList<Condition> Conditions,
    IReadOnlyList<MonitorAction> Actions)
{
    public static Situation Create(string name) =>
        new(Guid.NewGuid().ToString("N"), name, true, SituationMatch.All, [], []);

    // How long the user's own switch of a monitor holds before this situation takes over again while
    // it still applies; 0 keeps the user's choice until the situation changes. Persisted as minutes.
    public int ReassertMinutes { get; init; }

    public static readonly int[] ReassertChoices = [0, 15, 30, 60, 120, 240];

    public TimeSpan? ReassertAfter => ReassertMinutes > 0 ? TimeSpan.FromMinutes(ReassertMinutes) : null;

    // A situation with no conditions is never active, so a half-built one can't switch anything.
    public bool IsActive(SituationContext now) =>
        Enabled && Conditions.Count > 0 &&
        (Match == SituationMatch.All ? Conditions.All(c => c.IsMet(now)) : Conditions.Any(c => c.IsMet(now)));

    public SensorKind Sensors => Conditions.Aggregate(SensorKind.None, (kinds, c) => kinds | c.Sensor);

    public MonitorAction? ActionFor(string monitorId) => Actions.FirstOrDefault(a => a.MonitorId == monitorId);

    // Sets, replaces, or (null) removes the action for one monitor.
    public Situation WithAction(string monitorId, bool? turnOn) => this with
    {
        Actions = [.. Actions.Where(a => a.MonitorId != monitorId), .. turnOn is { } on ? [new MonitorAction(monitorId, on)] : Array.Empty<MonitorAction>()]
    };
}

enum SituationMatch
{
    All,
    Any
}

// What the situation does to one monitor (Monitors.All) while it's active.
sealed record MonitorAction(string MonitorId, bool TurnOn);

// What the PC looks like right now, as far as the running sensors know. A null field is unknown (its
// sensor is off, access was denied, there's no battery), and a condition that needs it isn't met
// either way round, so "not at home" doesn't fire just because location is off.
sealed record SituationContext(
    DateTime LocalTime,
    IReadOnlyCollection<string>? Networks,
    GeoPoint? Location,
    PowerState? Power,
    IReadOnlyCollection<string>? Devices)
{
    public static SituationContext Unknown(DateTime localTime) => new(localTime, null, null, null, null);
}

readonly record struct GeoPoint(double Latitude, double Longitude);

// BatteryPercent is null on a PC without a battery.
sealed record PowerState(int? BatteryPercent, bool PluggedIn);

// The sensors a condition reads, so the engine runs only the ones some situation needs.
[Flags]
enum SensorKind
{
    None = 0,
    Network = 1,
    Location = 2,
    Power = 4,
    Devices = 8,
    Clock = 16
}

// One test in a situation. Not flips it ("not connected to", "outside", "not between"). To add a kind:
// derive here, register it below for JSON, then describe it (SituationText), give it a sensor
// (SituationEngine) if it reads a new one, and an editor (SituationEditor).
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NetworkCondition), "network")]
[JsonDerivedType(typeof(PlaceCondition), "place")]
[JsonDerivedType(typeof(BatteryCondition), "battery")]
[JsonDerivedType(typeof(DeviceCondition), "device")]
[JsonDerivedType(typeof(TimeCondition), "time")]
abstract record Condition
{
    public bool Not { get; init; }

    public abstract SensorKind Sensor { get; }

    // The raw test, before Not; null when the answer isn't known.
    public abstract bool? Test(SituationContext now);

    public bool IsMet(SituationContext now) => Test(now) is { } result && result != Not;
}

// Connected to a network with this name: the Wi-Fi SSID or the name Windows gives a wired network.
sealed record NetworkCondition(string Network) : Condition
{
    public override SensorKind Sensor => SensorKind.Network;

    public override bool? Test(SituationContext now) =>
        string.IsNullOrWhiteSpace(Network) || now.Networks is not { } networks
            ? null
            : networks.Contains(Network.Trim(), StringComparer.OrdinalIgnoreCase);
}

// Within RadiusMeters of a point. Place is the user's name for it ("Home").
sealed record PlaceCondition(GeoPoint? Center, int RadiusMeters, string Place) : Condition
{
    public static readonly int[] RadiusChoices = [100, 250, 500, 1000, 5000];
    public const int DefaultRadius = 250;

    // Nothing to compare against until a place is set, so no reason to run location (or ask for it).
    public override SensorKind Sensor => Center is null ? SensorKind.None : SensorKind.Location;

    public override bool? Test(SituationContext now) =>
        Center is { } center && now.Location is { } here
            ? SituationPolicy.DistanceMeters(center, here) <= RadiusMeters
            : null;
}

enum BatteryCheck
{
    Below,      // battery under Percent
    AtLeast,    // battery at Percent or more
    PluggedIn,
    OnBattery
}

// Charge level or power source. The percentage checks are unknown on a PC without a battery.
sealed record BatteryCondition(BatteryCheck Check, int Percent) : Condition
{
    public const int DefaultPercent = 20;

    public override SensorKind Sensor => SensorKind.Power;

    public override bool? Test(SituationContext now) => (now.Power, Check) switch
    {
        (null, _) => null,
        ({ } power, BatteryCheck.PluggedIn) => power.PluggedIn,
        ({ } power, BatteryCheck.OnBattery) => !power.PluggedIn,
        ({ BatteryPercent: int percent }, BatteryCheck.Below) => percent < Percent,
        ({ BatteryPercent: int percent }, BatteryCheck.AtLeast) => percent >= Percent,
        _ => null
    };
}

// A device (headphones, a dock, a monitor, a mouse) is connected, matched by the name Windows shows
// in Settings > Bluetooth & devices. Names survive moving a USB device to another port; IDs don't.
sealed record DeviceCondition(string Device) : Condition
{
    public override SensorKind Sensor => SensorKind.Devices;

    public override bool? Test(SituationContext now) =>
        string.IsNullOrWhiteSpace(Device) || now.Devices is not { } devices
            ? null
            : devices.Contains(Device.Trim(), StringComparer.OrdinalIgnoreCase);
}

[Flags]
enum WeekDays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    Every = Weekdays | Weekend
}

// Between From and To on the chosen days. A window that ends at or before it starts runs overnight
// (22:00–06:00) and belongs to the day it starts on; From == To is a full 24 hours.
sealed record TimeCondition(TimeOnly From, TimeOnly To, WeekDays Days) : Condition
{
    public override SensorKind Sensor => SensorKind.Clock;

    public override bool? Test(SituationContext now)
    {
        var time = TimeOnly.FromDateTime(now.LocalTime);
        if (From < To)
            return OnDay(now.LocalTime) && time >= From && time < To;

        return time >= From ? OnDay(now.LocalTime) :
            time < To && OnDay(now.LocalTime.AddDays(-1));
    }

    // The next moment the answer can change: the next From or To after now.
    public DateTime NextChange(DateTime now) => Min(Next(From, now), Next(To, now));

    bool OnDay(DateTime day) => Days.HasFlag(DayFlag(day.DayOfWeek));

    public static WeekDays DayFlag(DayOfWeek day) => (WeekDays)(1 << (int)day);

    static DateTime Next(TimeOnly time, DateTime now)
    {
        var today = now.Date + time.ToTimeSpan();
        return today > now ? today : today.AddDays(1);
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
