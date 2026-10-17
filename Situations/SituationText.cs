using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Words for situations and their conditions, shared by the editor, the popout, and the tray icon.
// No WinRT here, so the test project links this file directly.
static class SituationText
{
    public static string Describe(Condition condition, IFormatProvider? culture = null) => condition switch
    {
        NetworkCondition c => $"{(c.Not ? "Not connected to" : "Connected to")} {Named(c.Network, "a network")}",
        PlaceCondition c => c.Not
            ? $"Farther than {Distance(c.RadiusMeters)} from {PlaceName(c)}"
            : $"Within {Distance(c.RadiusMeters)} of {PlaceName(c)}",
        BatteryCondition c => (c.Not ? Opposite(c.Check) : c.Check) switch
        {
            BatteryCheck.Below => $"Battery below {c.Percent}%",
            BatteryCheck.AtLeast => $"Battery at {c.Percent}% or more",
            BatteryCheck.PluggedIn => "Plugged in",
            _ => "On battery"
        },
        DeviceCondition c => $"{Named(c.Device, "A device")} {(c.Not ? "not connected" : "connected")}",
        TimeCondition c => UpperFirst($"{(c.Not ? "Outside " : "")}{Hours(c, culture)} {OnDays(c.Days, culture)}"),
        _ => condition.GetType().Name
    };

    // One line for a situation's conditions: "Connected to Fin Nest and battery below 20%".
    public static string Summary(Situation situation, IFormatProvider? culture = null)
    {
        if (situation.Conditions.Count == 0)
            return "No conditions yet";

        // Mid-sentence, lowercase the leading word, but not a device's own name ("LIFT connected").
        var parts = situation.Conditions.Select((c, i) => i == 0 || c is DeviceCondition { Device: var name } && !string.IsNullOrWhiteSpace(name)
            ? Describe(c, culture)
            : LowerFirst(Describe(c, culture)));
        return string.Join(situation.Match == SituationMatch.All ? " and " : " or ", parts);
    }

    public static string Name(Situation situation) =>
        string.IsNullOrWhiteSpace(situation.Name) ? "Untitled situation" : situation.Name.Trim();

    // Why a monitor is in its state, for the popout; null when it's simply the user's usual choice.
    public static string? Reason(MonitorSwitch monitor, Func<string, Situation?> findSituation)
    {
        if (monitor.Override is { } o)
            return $"Turned {OnOff(o.On)} by you, overriding “{NameOf(o.Overrode.SituationId)}”";

        return monitor.Demand is { } d ? $"Turned {OnOff(d.On)} by “{NameOf(d.SituationId)}”" : null;

        string NameOf(string id) => findSituation(id) is { } s ? Name(s) : "a situation";
    }

    public static string ActionLabel(bool? turnOn) => turnOn switch
    {
        true => "Turn on",
        false => "Turn off",
        null => "Leave as is"
    };

    // "250 m", "1 km", "1.5 km".
    public static string Distance(double meters) =>
        meters < 1000
            ? $"{Math.Round(meters / 10) * 10:0} m"
            : $"{(meters / 1000).ToString("0.#", CultureInfo.InvariantCulture)} km";

    public static string Coordinates(GeoPoint point) =>
        string.Create(CultureInfo.InvariantCulture, $"{point.Latitude:0.00000}, {point.Longitude:0.00000}");

    // "9:00 AM–5:00 PM", or "all day" for a 24-hour window starting at midnight.
    public static string Hours(TimeCondition condition, IFormatProvider? culture = null)
    {
        if (condition.From == condition.To)
            return condition.From == TimeOnly.MinValue ? "all day" : $"24 hours from {condition.From.ToString("t", culture)}";

        return $"{condition.From.ToString("t", culture)}–{condition.To.ToString("t", culture)}";
    }

    // "every day", "on weekdays", "on Mon, Wed, Fri".
    public static string OnDays(WeekDays days, IFormatProvider? culture = null) => days switch
    {
        WeekDays.Every => "every day",
        WeekDays.Weekdays => "on weekdays",
        WeekDays.Weekend => "on weekends",
        WeekDays.None => "on no days",
        _ => "on " + string.Join(", ", WeekOrder.Where(d => days.HasFlag(TimeCondition.DayFlag(d))).Select(d => ShortDay(d, culture)))
    };

    // Monday first, the way the editor lays out its day buttons.
    public static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static string ShortDay(DayOfWeek day, IFormatProvider? culture = null) =>
        DateTimeFormatInfo.GetInstance(culture).AbbreviatedDayNames[(int)day];

    static string PlaceName(PlaceCondition c) =>
        !string.IsNullOrWhiteSpace(c.Place) ? c.Place.Trim() :
        c.Center is { } center ? Coordinates(center) :
        "a place";

    static string Named(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

    static string OnOff(bool on) => on ? "on" : "off";

    static BatteryCheck Opposite(BatteryCheck check) => check switch
    {
        BatteryCheck.Below => BatteryCheck.AtLeast,
        BatteryCheck.AtLeast => BatteryCheck.Below,
        BatteryCheck.PluggedIn => BatteryCheck.OnBattery,
        _ => BatteryCheck.PluggedIn
    };

    static string LowerFirst(string text) =>
        text.Length > 0 && char.IsUpper(text[0]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    static string UpperFirst(string text) =>
        text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
}
