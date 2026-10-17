using System;
using System.Collections.Generic;
using System.Linq;

// A situation's say over one monitor: on or off, and which situation decided.
readonly record struct SituationDemand(string SituationId, bool On);

// The user switched a monitor by hand while a situation was in charge of it. It holds until that
// situation's demand changes (the situation ends, or another one takes over), then situations decide again.
readonly record struct UserOverride(SituationDemand Overrode, bool On);

// One monitor's switch: the user's usual choice (persisted; what happens when no situation cares),
// the current situation demand, and any override of it.
sealed record MonitorSwitch(bool Usual, SituationDemand? Demand, UserOverride? Override)
{
    public bool IsOn => Override?.On ?? Demand?.On ?? Usual;

    public MonitorSwitch WithDemand(SituationDemand? demand) => this with
    {
        Demand = demand,
        Override = Override is { } o && o.Overrode == demand ? o : null
    };

    // The user flipped the switch. With no situation in charge that's the new usual; otherwise it
    // overrides the situation (or, if it agrees with it, drops an earlier override).
    public MonitorSwitch Choose(bool on) => Demand is { } demand
        ? this with { Override = on == demand.On ? null : new UserOverride(demand, on) }
        : this with { Usual = on, Override = null };
}

// Deciding which situations hold and what they ask of each monitor. Pure, so it's unit tested.
static class SituationPolicy
{
    const double EarthRadiusMeters = 6_371_000;

    // The latest a clock re-check can be, even with no time condition due: a long timer may fire late
    // after sleep, and the clock can be changed.
    public static readonly TimeSpan MaxClockWait = TimeSpan.FromHours(1);

    // Every monitor that an active situation acts on, mapped to its demand. When several active
    // situations act on the same monitor, the one listed first wins.
    public static Dictionary<string, SituationDemand> Demands(IEnumerable<Situation> situations, SituationContext now)
    {
        var demands = new Dictionary<string, SituationDemand>();
        foreach (var situation in situations.Where(s => s.IsActive(now)))
        {
            foreach (var action in situation.Actions)
                demands.TryAdd(action.MonitorId, new SituationDemand(situation.Id, action.TurnOn));
        }

        return demands;
    }

    public static SensorKind SensorsFor(IEnumerable<Situation> situations) =>
        situations.Aggregate(SensorKind.None, (kinds, s) => kinds | s.Sensors);

    // When to re-check the clock: the earliest time condition boundary, at most MaxClockWait away.
    public static DateTime NextClockCheck(IEnumerable<Situation> situations, DateTime now)
    {
        var next = now + MaxClockWait;
        foreach (var condition in situations.SelectMany(s => s.Conditions).OfType<TimeCondition>())
        {
            var change = condition.NextChange(now);
            if (change < next)
                next = change;
        }

        return next;
    }

    // Great-circle distance (haversine); plenty accurate at the radii a place uses.
    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        double dLat = ToRadians(b.Latitude - a.Latitude);
        double dLon = ToRadians(b.Longitude - a.Longitude);
        double h = Math.Pow(Math.Sin(dLat / 2), 2) +
                   Math.Cos(ToRadians(a.Latitude)) * Math.Cos(ToRadians(b.Latitude)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
