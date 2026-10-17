using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

[TestClass]
internal sealed class SituationTests
{
    static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
    static readonly DateTime Wednesday10am = new(2026, 10, 14, 10, 0, 0);
    static readonly GeoPoint Home = new(43.0389, -87.9065);

    static SituationContext Context(
        DateTime? time = null,
        string[]? networks = null,
        GeoPoint? location = null,
        PowerState? power = null,
        string[]? devices = null) =>
        new(time ?? Wednesday10am, networks, location, power, devices);

    static Situation With(params Condition[] conditions) =>
        Situation.Create("Test") with { Conditions = conditions, Actions = [new MonitorAction("internet", true)] };

    [TestMethod]
    public void NetworkCondition_MatchesAnyConnectedNameIgnoringCase()
    {
        var condition = new NetworkCondition("fin nest");
        Assert.IsTrue(condition.IsMet(Context(networks: ["vEthernet", "Fin Nest"])));
        Assert.IsFalse(condition.IsMet(Context(networks: ["Coffee Shop"])));
        Assert.IsTrue((condition with { Not = true }).IsMet(Context(networks: ["Coffee Shop"])));
    }

    [TestMethod]
    public void UnknownSensorData_MeetsNeitherPolarity()
    {
        Condition[] conditions =
        [
            new NetworkCondition("Fin Nest"),
            new PlaceCondition(Home, 250, "Home"),
            new BatteryCondition(BatteryCheck.Below, 20),
            new DeviceCondition("LIFT"),
        ];

        foreach (var condition in conditions)
        {
            Assert.IsFalse(condition.IsMet(Context()), condition.ToString());
            Assert.IsFalse((condition with { Not = true }).IsMet(Context()), condition.ToString());
        }
    }

    [TestMethod]
    public void BlankNameOrUnsetPlace_IsNeverMet()
    {
        Assert.IsFalse(new NetworkCondition("").IsMet(Context(networks: ["Fin Nest"])));
        Assert.IsFalse((new NetworkCondition("") with { Not = true }).IsMet(Context(networks: ["Fin Nest"])));
        Assert.IsFalse((new DeviceCondition(" ") with { Not = true }).IsMet(Context(devices: ["LIFT"])));
        Assert.IsFalse(new PlaceCondition(null, 250, "").IsMet(Context(location: Home)));
    }

    [TestMethod]
    public void PlaceCondition_UsesTheRadius()
    {
        var nearby = new GeoPoint(Home.Latitude + 0.001, Home.Longitude); // ~111 m north
        Assert.IsTrue(new PlaceCondition(Home, 250, "Home").IsMet(Context(location: nearby)));
        Assert.IsFalse(new PlaceCondition(Home, 100, "Home").IsMet(Context(location: nearby)));
        Assert.IsTrue((new PlaceCondition(Home, 100, "Home") with { Not = true }).IsMet(Context(location: nearby)));
    }

    [TestMethod]
    public void DistanceMeters_IsCloseToKnownDistances()
    {
        // One degree of latitude is about 111.2 km.
        Assert.AreEqual(111_195, SituationPolicy.DistanceMeters(new GeoPoint(0, 0), new GeoPoint(1, 0)), 50);
        Assert.AreEqual(0, SituationPolicy.DistanceMeters(Home, Home), 0.001);
    }

    [TestMethod]
    [DataRow(BatteryCheck.Below, 20, 15, true, true)]
    [DataRow(BatteryCheck.Below, 20, 20, true, false)]
    [DataRow(BatteryCheck.AtLeast, 20, 20, true, true)]
    [DataRow(BatteryCheck.PluggedIn, 0, 50, true, true)]
    [DataRow(BatteryCheck.PluggedIn, 0, 50, false, false)]
    [DataRow(BatteryCheck.OnBattery, 0, 50, false, true)]
    public void BatteryCondition_ChecksChargeAndSource(BatteryCheck check, int threshold, int percent, bool pluggedIn, bool expected)
    {
        Assert.AreEqual(expected, new BatteryCondition(check, threshold).IsMet(Context(power: new PowerState(percent, pluggedIn))));
    }

    [TestMethod]
    public void BatteryCondition_ChargeChecksAreUnknownWithoutABattery()
    {
        var desktop = Context(power: new PowerState(null, true));
        Assert.IsFalse(new BatteryCondition(BatteryCheck.Below, 20).IsMet(desktop));
        Assert.IsFalse(new BatteryCondition(BatteryCheck.AtLeast, 20).IsMet(desktop));
        Assert.IsTrue(new BatteryCondition(BatteryCheck.PluggedIn, 20).IsMet(desktop));
    }

    [TestMethod]
    public void DeviceCondition_MatchesByName()
    {
        Assert.IsTrue(new DeviceCondition("Microsoft Audio Dock").IsMet(Context(devices: ["LIFT", "microsoft audio dock"])));
        Assert.IsTrue((new DeviceCondition("LIFT") with { Not = true }).IsMet(Context(devices: [])));
    }

    [TestMethod]
    [DataRow(9, 0, true)]
    [DataRow(16, 59, true)]
    [DataRow(17, 0, false)]  // To is exclusive
    [DataRow(8, 59, false)]
    public void TimeCondition_DaytimeWindow(int hour, int minute, bool expected)
    {
        var workHours = new TimeCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Weekdays);
        Assert.AreEqual(expected, workHours.IsMet(Context(time: Wednesday10am.Date.AddHours(hour).AddMinutes(minute))));
    }

    [TestMethod]
    public void TimeCondition_RespectsDays()
    {
        var workHours = new TimeCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Weekdays);
        var saturday10am = new DateTime(2026, 10, 17, 10, 0, 0);
        Assert.IsFalse(workHours.IsMet(Context(time: saturday10am)));
        Assert.IsTrue((workHours with { Not = true }).IsMet(Context(time: saturday10am)));
    }

    [TestMethod]
    public void TimeCondition_OvernightWindowBelongsToTheDayItStarts()
    {
        // Friday nights only: 22:00 Friday to 06:00 Saturday.
        var fridayNight = new TimeCondition(new TimeOnly(22, 0), new TimeOnly(6, 0), WeekDays.Friday);
        var friday = new DateTime(2026, 10, 16);
        Assert.IsTrue(fridayNight.IsMet(Context(time: friday.AddHours(23))));
        Assert.IsTrue(fridayNight.IsMet(Context(time: friday.AddDays(1).AddHours(5))));   // Saturday 05:00
        Assert.IsFalse(fridayNight.IsMet(Context(time: friday.AddDays(1).AddHours(23)))); // Saturday 23:00
        Assert.IsFalse(fridayNight.IsMet(Context(time: friday.AddHours(5))));             // Friday 05:00 is Thursday's night
    }

    [TestMethod]
    public void TimeCondition_EqualFromAndToIsTwentyFourHours()
    {
        var weekendAllDay = new TimeCondition(TimeOnly.MinValue, TimeOnly.MinValue, WeekDays.Weekend);
        var saturday = new DateTime(2026, 10, 17);
        Assert.IsTrue(weekendAllDay.IsMet(Context(time: saturday)));
        Assert.IsTrue(weekendAllDay.IsMet(Context(time: saturday.AddHours(23.99))));
        Assert.IsFalse(weekendAllDay.IsMet(Context(time: saturday.AddDays(-1).AddHours(12))));
    }

    [TestMethod]
    public void NextChange_IsTheNextBoundaryAfterNow()
    {
        var workHours = new TimeCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Weekdays);
        Assert.AreEqual(Wednesday10am.Date.AddHours(17), workHours.NextChange(Wednesday10am));
        Assert.AreEqual(Wednesday10am.Date.AddDays(1).AddHours(9), workHours.NextChange(Wednesday10am.Date.AddHours(17)));
    }

    [TestMethod]
    public void NextClockCheck_IsCappedWithoutTimeConditions()
    {
        Assert.AreEqual(Wednesday10am + SituationPolicy.MaxClockWait, SituationPolicy.NextClockCheck([With(new NetworkCondition("x"))], Wednesday10am));

        var soon = With(new TimeCondition(new TimeOnly(10, 15), new TimeOnly(11, 0), WeekDays.Every));
        Assert.AreEqual(Wednesday10am.AddMinutes(15), SituationPolicy.NextClockCheck([soon], Wednesday10am));
    }

    [TestMethod]
    public void Situation_MatchAllAndAny()
    {
        var atHome = new NetworkCondition("Fin Nest");
        var lowBattery = new BatteryCondition(BatteryCheck.Below, 20);
        var context = Context(networks: ["Fin Nest"], power: new PowerState(80, false));

        Assert.IsFalse(With(atHome, lowBattery).IsActive(context));
        Assert.IsTrue((With(atHome, lowBattery) with { Match = SituationMatch.Any }).IsActive(context));
    }

    [TestMethod]
    public void Situation_WithoutConditionsOrDisabled_IsNeverActive()
    {
        var context = Context(networks: ["Fin Nest"]);
        Assert.IsFalse(With().IsActive(context));
        Assert.IsFalse((With(new NetworkCondition("Fin Nest")) with { Enabled = false }).IsActive(context));
    }

    [TestMethod]
    public void Demands_FirstActiveSituationWinsPerMonitor()
    {
        var context = Context(networks: ["Fin Nest"]);
        var first = With(new NetworkCondition("Fin Nest")).WithAction("internet", false);
        var second = With(new NetworkCondition("Fin Nest")).WithAction("internet", true).WithAction("other", true);
        var inactive = With(new NetworkCondition("Elsewhere")).WithAction("third", true);

        var demands = SituationPolicy.Demands([inactive, first, second], context);

        Assert.AreEqual(new SituationDemand(first.Id, false), demands["internet"]);
        Assert.AreEqual(new SituationDemand(second.Id, true), demands["other"]);
        Assert.IsFalse(demands.ContainsKey("third"));
    }

    [TestMethod]
    public void WithAction_SetsReplacesAndRemoves()
    {
        var situation = Situation.Create("x").WithAction("internet", true).WithAction("internet", false);
        Assert.AreEqual(1, situation.Actions.Count);
        Assert.IsFalse(situation.ActionFor("internet")!.TurnOn);
        Assert.IsNull(situation.WithAction("internet", null).ActionFor("internet"));
    }

    [TestMethod]
    public void SensorsFor_CombinesEveryConditionKind()
    {
        var situation = With(new NetworkCondition("a"), new TimeCondition(default, default, WeekDays.Every));
        Assert.AreEqual(SensorKind.Network | SensorKind.Clock, SituationPolicy.SensorsFor([situation]));
    }

    [TestMethod]
    public void PlaceCondition_NeedsLocationOnlyOnceAPlaceIsSet()
    {
        Assert.AreEqual(SensorKind.None, new PlaceCondition(null, 250, "Home").Sensor);
        Assert.AreEqual(SensorKind.Location, new PlaceCondition(Home, 250, "Home").Sensor);
    }

    [TestMethod]
    public void MonitorSwitch_SituationDemandBeatsTheUsualChoice()
    {
        var monitor = new MonitorSwitch(Usual: false, null, null);
        Assert.IsFalse(monitor.IsOn);
        Assert.IsTrue(monitor.WithDemand(new SituationDemand("work", true)).IsOn);
        Assert.IsFalse(monitor.WithDemand(new SituationDemand("work", true)).WithDemand(null).IsOn);
    }

    [TestMethod]
    public void MonitorSwitch_OverrideLastsUntilTheDemandChanges()
    {
        var work = new SituationDemand("work", true);
        var monitor = new MonitorSwitch(Usual: false, null, null).WithDemand(work).Choose(false);

        Assert.IsFalse(monitor.IsOn);
        Assert.IsFalse(monitor.Usual, "an override doesn't change the usual choice");
        Assert.IsFalse(monitor.WithDemand(work).IsOn, "same demand again keeps the override");

        var home = new SituationDemand("home", true);
        Assert.IsTrue(monitor.WithDemand(home).IsOn, "a different situation takes over");
        Assert.IsNull(monitor.WithDemand(null).Override);
    }

    [TestMethod]
    public void MonitorSwitch_ChoosingWhatTheSituationWantsDropsTheOverride()
    {
        var work = new SituationDemand("work", true);
        var monitor = new MonitorSwitch(false, null, null).WithDemand(work).Choose(false).Choose(true);
        Assert.IsNull(monitor.Override);
        Assert.IsTrue(monitor.IsOn);
    }

    [TestMethod]
    public void MonitorSwitch_ExpiredOverrideLetsTheSituationReassert()
    {
        var work = new SituationDemand("work", true);
        var start = new DateTime(2026, 1, 5, 9, 0, 0);
        var monitor = new MonitorSwitch(false, null, null).WithDemand(work, start).Choose(false, start.AddMinutes(30));

        Assert.IsFalse(monitor.WithDemand(work, start.AddMinutes(29)).IsOn, "still within the grace period");
        Assert.IsTrue(monitor.WithDemand(work, start.AddMinutes(30)).IsOn, "the situation takes over at the expiry");
        Assert.IsNull(monitor.WithDemand(work, start.AddMinutes(31)).Override);
        Assert.IsFalse(monitor.Usual);
    }

    [TestMethod]
    public void Situation_ReassertAfterIsOffByDefaultAndSurvivesJson()
    {
        Assert.IsNull(Situation.Create("x").ReassertAfter);

        var situation = Situation.Create("x") with { ReassertMinutes = 60 };
        string json = SituationJson.Write([situation]);
        Assert.AreEqual(TimeSpan.FromHours(1), SituationJson.Read(json).Single().ReassertAfter);
        Assert.IsNull(SituationJson.Read(
            """{"version": 1, "situations": [{"id": "a", "name": "x", "enabled": true, "match": "All", "conditions": [], "actions": []}]}""")
            .Single().ReassertAfter, "files from before this setting read as off");
    }

    [TestMethod]
    public void Reason_MentionsWhenAnOverrideEnds()
    {
        var work = Situation.Create("At work");
        var demand = new SituationDemand(work.Id, true);
        var monitor = new MonitorSwitch(false, demand, new UserOverride(demand, false, new DateTime(2026, 1, 5, 15, 15, 0)));
        Assert.AreEqual("Turned off by you, overriding “At work” until 3:15 PM", SituationText.Reason(monitor, _ => work, EnUs));
    }

    [TestMethod]
    public void MonitorSwitch_WithoutADemandChoosingSetsTheUsual()
    {
        var monitor = new MonitorSwitch(false, null, null).Choose(true);
        Assert.IsTrue(monitor.Usual);
        Assert.IsNull(monitor.Override);
    }

    [TestMethod]
    public void Json_RoundTripsEveryConditionKind()
    {
        var situation = Situation.Create("Work") with
        {
            Match = SituationMatch.Any,
            Conditions =
            [
                new NetworkCondition("Fin Nest") { Not = true },
                new PlaceCondition(Home, 500, "Office"),
                new PlaceCondition(null, 250, ""),
                new BatteryCondition(BatteryCheck.Below, 15),
                new DeviceCondition("Microsoft Audio Dock"),
                new TimeCondition(new TimeOnly(22, 30), new TimeOnly(6, 0), WeekDays.Friday | WeekDays.Saturday),
            ],
            Actions = [new MonitorAction("internet", false)]
        };

        string json = SituationJson.Write([situation]);
        var read = SituationJson.Read(json).Single();

        StringAssert.Contains(json, "\"kind\": \"network\"");
        StringAssert.Contains(json, "\"days\": \"Friday, Saturday\"");
        Assert.DoesNotContain("\"sensor", json, "computed members stay out of the file");
        Assert.AreEqual(situation.Id, read.Id);
        Assert.AreEqual(situation.Match, read.Match);
        CollectionAssert.AreEqual(situation.Conditions.ToArray(), read.Conditions.ToArray());
        CollectionAssert.AreEqual(situation.Actions.ToArray(), read.Actions.ToArray());
    }

    [TestMethod]
    public void Json_RejectsANewerFileAndUnknownKinds()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => SituationJson.Read("""{"version": 99, "situations": []}"""));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => SituationJson.Read(
            """{"version": 1, "situations": [{"id": "a", "name": "x", "enabled": true, "match": "All", "conditions": [{"kind": "moon"}], "actions": []}]}"""));
    }

    [TestMethod]
    public void Describe_ReadsNaturally()
    {
        Assert.AreEqual("Connected to Fin Nest", SituationText.Describe(new NetworkCondition("Fin Nest")));
        Assert.AreEqual("Not connected to a network", SituationText.Describe(new NetworkCondition("") { Not = true }));
        Assert.AreEqual("Within 250 m of Home", SituationText.Describe(new PlaceCondition(Home, 250, "Home")));
        Assert.AreEqual("Farther than 1 km from 43.03890, -87.90650", SituationText.Describe(new PlaceCondition(Home, 1000, "") { Not = true }));
        Assert.AreEqual("Battery below 20%", SituationText.Describe(new BatteryCondition(BatteryCheck.Below, 20)));
        Assert.AreEqual("Battery at 20% or more", SituationText.Describe(new BatteryCondition(BatteryCheck.Below, 20) { Not = true }));
        Assert.AreEqual("On battery", SituationText.Describe(new BatteryCondition(BatteryCheck.OnBattery, 20)));
        Assert.AreEqual("LIFT not connected", SituationText.Describe(new DeviceCondition("LIFT") { Not = true }));
        Assert.AreEqual("9:00 AM–5:00 PM on weekdays", SituationText.Describe(new TimeCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Weekdays), EnUs));
        Assert.AreEqual("Outside 10:00 PM–6:00 AM on Mon, Fri", SituationText.Describe(new TimeCondition(new TimeOnly(22, 0), new TimeOnly(6, 0), WeekDays.Monday | WeekDays.Friday) { Not = true }, EnUs));
        Assert.AreEqual("All day every day", SituationText.Describe(new TimeCondition(TimeOnly.MinValue, TimeOnly.MinValue, WeekDays.Every), EnUs));
    }

    [TestMethod]
    public void Summary_JoinsConditionsWithTheMatchWord()
    {
        var situation = With(new NetworkCondition("Fin Nest"), new DeviceCondition("LIFT"), new BatteryCondition(BatteryCheck.PluggedIn, 0));
        Assert.AreEqual("Connected to Fin Nest and LIFT connected and plugged in", SituationText.Summary(situation));
        Assert.AreEqual("Connected to Fin Nest or LIFT connected or plugged in", SituationText.Summary(situation with { Match = SituationMatch.Any }));
        Assert.AreEqual("No conditions yet", SituationText.Summary(With()));
        Assert.AreEqual("Plugged in and a device connected", SituationText.Summary(With(new BatteryCondition(BatteryCheck.PluggedIn, 0), new DeviceCondition(""))));
    }

    [TestMethod]
    public void Reason_NamesTheSituationInCharge()
    {
        var work = Situation.Create("At work");
        Situation? Find(string id) => id == work.Id ? work : null;
        var demand = new SituationDemand(work.Id, true);

        Assert.IsNull(SituationText.Reason(new MonitorSwitch(true, null, null), Find));
        Assert.AreEqual("Turned on by “At work”", SituationText.Reason(new MonitorSwitch(false, demand, null), Find));
        Assert.AreEqual("Turned off by you, overriding “At work”", SituationText.Reason(new MonitorSwitch(false, demand, new UserOverride(demand, false)), Find));
    }

    [TestMethod]
    [DataRow(100.0, "100 m")]
    [DataRow(254.0, "250 m")]
    [DataRow(1000.0, "1 km")]
    [DataRow(1500.0, "1.5 km")]
    public void Distance_RoundsForReading(double meters, string expected)
    {
        Assert.AreEqual(expected, SituationText.Distance(meters));
    }
}
