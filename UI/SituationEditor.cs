using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;          // DispatcherQueue, DispatcherQueueTimer
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // Component, Element, MenuFlyoutItemBase
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;                 // TextWrapping, Thickness
using static Microsoft.UI.Reactor.Factories;

record SituationEditorProps(Situation Situation, SituationState State, bool IsFirst, bool IsLast);

// The right side of the situations window: one situation's name, on/off, conditions ("When"),
// actions on each registered monitor ("Then"), and its place in the priority order.
class SituationEditor : Component<SituationEditorProps>
{
    static readonly TimeSpan NameCommitDelay = TimeSpan.FromMilliseconds(300);
    static readonly string[] MatchLabels = ["all of these are true", "any of these is true"];
    static readonly string[] ActionLabels = [SituationText.ActionLabel(null), SituationText.ActionLabel(true), SituationText.ActionLabel(false)];

    public override Element Render()
    {
        var situation = Props.Situation;
        var state = Props.State;
        var (confirmingDelete, setConfirmingDelete) = UseState(false);
        var (name, setName) = UseNameDraft(situation.Id, situation.Name);

        void Update(Situation next) => SituationEngine.Update(next);
        void SetCondition(int index, Condition condition) =>
            Update(situation with { Conditions = [.. situation.Conditions.Select((c, i) => i == index ? condition : c)] });
        void RemoveCondition(int index) =>
            Update(situation with { Conditions = [.. situation.Conditions.Where((_, i) => i != index)] });
        void AddCondition(Condition condition) =>
            Update(situation with { Conditions = [.. situation.Conditions, condition] });

        var conditions = situation.Conditions
            .Select((condition, index) => Component<ConditionCard, ConditionCardProps>(new(condition, index, state, SetCondition, RemoveCondition))
                .WithKey($"{index}:{condition.GetType().Name}"))
            .ToArray();

        // Keyed, because two of these come and go with the condition count; unkeyed, the siblings
        // after them would be reconciled against the wrong elements.
        var content = FlexColumn(
            (FlexRow(
                TextBox(name, setName, placeholderText: "Name this situation", header: "Name")
                    .AutomationId("SituationName")
                    .Flex(grow: 1, basis: 0),
                ToggleSwitch(situation.Enabled, on => Update(situation with { Enabled = on }), onContent: "On", offContent: "Off", header: "Situation")
                    .AutomationName("Situation on")
                    .MinWidth(0)
                    .Flex(shrink: 0)
            ) with { ColumnGap = 16, AlignItems = FlexAlign.End }).WithKey("name"),
            Status(situation, state.IsActive(situation.Id)).WithKey("status"),

            Section("When").WithKey("when"),
            situation.Conditions.Count > 1
                ? (FlexRow(
                    Body("Active when"),
                    ComboBox(MatchLabels, (int)situation.Match, i => Update(situation with { Match = (SituationMatch)Math.Clamp(i, 0, 1) }))
                        .AutomationName("Active when")
                ) with { ColumnGap = 8, AlignItems = FlexAlign.Center }).WithKey("match")
                : null,
            situation.Conditions.Count == 0
                ? Caption("Add a condition. A situation with none is never active.").Foreground(Theme.SecondaryText).WithKey("none")
                : null,
            (FlexColumn(conditions) with { RowGap = 8 }).WithKey("conditions"),
            AddConditionButton(state, AddCondition).WithKey("add"),

            Section("Then").WithKey("then"),
            (FlexColumn(Monitors.All.Select(m => MonitorRow(m, situation, Update).WithKey(m.Id)).ToArray()) with { RowGap = 8 }).WithKey("monitors"),
            Caption("When the situation ends, each monitor goes back to how you left it, unless another situation is in charge. " +
                    "Switching a monitor yourself while a situation is in charge lasts until the situation changes.")
                .Foreground(Theme.SecondaryText)
                .TextWrapping(TextWrapping.WrapWholeWords)
                .WithKey("ends"),

            Footer(situation, Props.IsFirst, Props.IsLast, confirmingDelete, setConfirmingDelete).WithKey("footer")
        ) with { RowGap = 12 };

        return ScrollView(Border(content).Padding(24, 16, 24, 24).MaxWidth(720));
    }

    // The name box edits a local draft and commits it after a pause in typing; committing per
    // keystroke re-renders the whole window, list included, which flashes. A draft still pending when
    // the editor unmounts (another situation selected, window closed) is committed then.
    (string name, Action<string> setName) UseNameDraft(string id, string committed)
    {
        var (draft, setDraft) = UseState(committed);
        var pending = UseRef<string?>(null);
        var timer = UseMemo(() =>
        {
            var t = DispatcherQueue.GetForCurrentThread().CreateTimer();
            t.Interval = NameCommitDelay;
            t.IsRepeating = false;
            return t;
        });

        UseEffect(() =>
        {
            void Commit()
            {
                timer.Stop();
                if (pending.Current is { } name)
                {
                    pending.Current = null;
                    SituationEngine.Rename(id, name);
                }
            }

            void OnTick(DispatcherQueueTimer sender, object args) => Commit();
            timer.Tick += OnTick;
            return () =>
            {
                timer.Tick -= OnTick;
                Commit();
            };
        });

        void SetName(string name)
        {
            setDraft(name);
            pending.Current = name;
            timer.Stop();
            timer.Start();
        }

        return (draft, SetName);
    }

    static Element Status(Situation situation, bool active)
    {
        string text =
            !situation.Enabled ? "This situation is off, so it won't switch anything." :
            situation.Conditions.Count == 0 ? "Not active: it has no conditions yet." :
            active ? "Active now." :
            "Not active right now.";

        return FlexRow(
            SituationsEditor.StatusPill(active && situation.Enabled ? "Active" : situation.Enabled ? "Waiting" : "Off", active && situation.Enabled),
            Caption(text).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.WrapWholeWords).Flex(grow: 1, shrink: 1, basis: 0)
        ) with { ColumnGap = 8, AlignItems = FlexAlign.Center };
    }

    static Element Section(string title) => Subtitle(title).Margin(0, 12, 0, 0);

    static Element AddConditionButton(SituationState state, Action<Condition> add)
    {
        string currentNetwork = state.Context.Networks?.FirstOrDefault() ?? "";
        MenuFlyoutItemBase[] items =
        [
            MenuItem("Network", () => add(new NetworkCondition(currentNetwork)), ConditionCard.NetworkGlyph),
            MenuItem("Place", () => add(new PlaceCondition(null, PlaceCondition.DefaultRadius, "")), ConditionCard.PlaceGlyph),
            MenuItem("Battery and power", () => add(new BatteryCondition(BatteryCheck.Below, BatteryCondition.DefaultPercent)), ConditionCard.BatteryGlyph),
            MenuItem("Device", () => add(new DeviceCondition("")), ConditionCard.DeviceGlyph),
            MenuItem("Time of day", () => add(new TimeCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Weekdays)), ConditionCard.TimeGlyph),
        ];

        return DropDownButton("Add a condition", MenuItems(items)).AutomationId("AddCondition");
    }

    static Element MonitorRow(IMonitor monitor, Situation situation, Action<Situation> update)
    {
        bool? turnOn = situation.ActionFor(monitor.Id)?.TurnOn;
        int index = turnOn switch { null => 0, true => 1, false => 2 };

        return Card(
            FlexRow(
                Icon(FontIcon(monitor.Glyph, fontSize: 16)),
                Body(monitor.Name).Flex(grow: 1, shrink: 1, basis: 0),
                ComboBox(ActionLabels, index, i => update(situation.WithAction(monitor.Id, i switch { 1 => true, 2 => false, _ => null })))
                    .AutomationName(monitor.Name)
                    .MinWidth(140)
                    .Flex(shrink: 0)
            ) with { ColumnGap = 12, AlignItems = FlexAlign.Center });
    }

    // Delete asks inline ("Delete “At home”?  Delete  Cancel") rather than in a flyout.
    static Element Footer(Situation situation, bool isFirst, bool isLast, bool confirming, Action<bool> setConfirming)
    {
        Element[] deleteControls = confirming
            ?
            [
                Body($"Delete “{SituationText.Name(situation)}”?").WithKey("ask"),
                Button("Delete", () => SituationEngine.Remove(situation.Id)).AccentButton().AutomationId("ConfirmDelete").WithKey("yes"),
                Button("Cancel", () => setConfirming(false)).AutomationId("CancelDelete").WithKey("no"),
            ]
            : [Button("Delete situation", () => setConfirming(true)).AutomationId("DeleteSituation").WithKey("delete")];

        return FlexRow(
            [
                Button("Move up", () => SituationEngine.Move(situation.Id, -1)).IsEnabled(!isFirst).ToolTip("Higher situations win when two switch the same monitor").WithKey("up"),
                Button("Move down", () => SituationEngine.Move(situation.Id, 1)).IsEnabled(!isLast).WithKey("down"),
                Border(Empty()).Flex(grow: 1).WithKey("space"),
                .. deleteControls
            ]) with { ColumnGap = 8, AlignItems = FlexAlign.Center, FlexPadding = new Thickness(0, 16, 0, 0) };
    }
}

record ConditionCardProps(
    Condition Condition,
    int Index,
    SituationState State,
    Action<int, Condition> OnChange,
    Action<int> OnRemove);

// One condition: its kind, whether it holds right now, its controls, and a remove button. Each kind
// gets its own controls below; a new condition kind adds a case to Controls and to Kind.
class ConditionCard : Component<ConditionCardProps>
{
    public const string NetworkGlyph = "";  // Wifi
    public const string PlaceGlyph = "";    // MapPin
    public const string BatteryGlyph = "";  // Battery10
    public const string DeviceGlyph = "";   // Devices
    public const string TimeGlyph = "";     // Recent (clock)

    static readonly string[] RadiusLabels = PlaceCondition.RadiusChoices.Select(m => SituationText.Distance(m)).ToArray();
    static readonly string[] BatteryLabels = ["Battery below", "Battery at or above", "Plugged in", "On battery"];

    public override Element Render()
    {
        var (locate, setLocate) = UseState(Locate.Idle);
        var condition = Props.Condition;
        var state = Props.State;
        void Set(Condition next) => Props.OnChange(Props.Index, next);

        // Asks for location access first (the Windows prompt, the first time), then takes a fresh fix.
        async Task LocateHere(PlaceCondition place)
        {
            setLocate(Locate.Finding);
            if (await LocationSensor.RequestAccessAsync() == LocationAccess.Denied)
            {
                setLocate(Locate.Denied);
                return;
            }

            var here = await LocationSensor.GetFixAsync(TimeSpan.Zero, precise: true);
            setLocate(here is null ? Locate.Failed : Locate.Idle);
            if (here is { } point)
                Set(place with { Center = point });
        }

        var (glyph, title) = Kind(condition);
        var header = FlexRow(
            Icon(FontIcon(glyph, fontSize: 16)),
            BodyStrong(title).Flex(grow: 1, shrink: 1, basis: 0),
            Liveness(condition, state),
            Button(Icon(FontIcon("", fontSize: 14)), () => Props.OnRemove(Props.Index))
                .SubtleButton()
                .Width(32)
                .Height(32)
                .Padding(0)
                .AutomationName($"Remove condition: {SituationText.Describe(condition)}")
                .ToolTip("Remove condition")
        ) with { ColumnGap = 12, AlignItems = FlexAlign.Center };

        Element controls = condition switch
        {
            NetworkCondition c => NetworkControls(c, state, Set),
            PlaceCondition c => PlaceControls(c, state, Set, locate, () => _ = LocateHere(c)),
            BatteryCondition c => BatteryControls(c, state, Set),
            DeviceCondition c => DeviceControls(c, state, Set),
            TimeCondition c => TimeControls(c, Set),
            _ => Caption(SituationText.Describe(condition))
        };

        return Card(FlexColumn(header, controls) with { RowGap = 12 });
    }

    static (string glyph, string title) Kind(Condition condition) => condition switch
    {
        NetworkCondition => (NetworkGlyph, "Network"),
        PlaceCondition => (PlaceGlyph, "Place"),
        BatteryCondition => (BatteryGlyph, "Battery and power"),
        DeviceCondition => (DeviceGlyph, "Device"),
        TimeCondition => (TimeGlyph, "Time of day"),
        _ => ("", condition.GetType().Name) // Unknown
    };

    // Live answer for this condition, so the user can see a rule work as they build it.
    static Element Liveness(Condition condition, SituationState state)
    {
        if (condition.Test(state.Context) is null)
        {
            string why = condition switch
            {
                NetworkCondition c when string.IsNullOrWhiteSpace(c.Network) => "Choose a network",
                DeviceCondition c when string.IsNullOrWhiteSpace(c.Device) => "Choose a device",
                PlaceCondition { Center: null } => "Set the place",
                PlaceCondition when state.LocationAccess == LocationAccess.Denied => "Location is off",
                BatteryCondition when state.Context.Power is { BatteryPercent: null } => "No battery",
                _ => "Checking…"
            };
            return SituationsEditor.StatusPill(why, false);
        }

        return condition.IsMet(state.Context)
            ? SituationsEditor.StatusPill("True now", true)
            : SituationsEditor.StatusPill("Not now", false);
    }

    static Element NetworkControls(NetworkCondition c, SituationState state, Action<Condition> set) =>
        FlexColumn(
            Wrapping(
                ComboBox(["Connected to", "Not connected to"], c.Not ? 1 : 0, i => set(c with { Not = i == 1 }))
                    .AutomationName("Network test")
                    .MinWidth(170),
                TextBox(c.Network, v => set(c with { Network = v }), placeholderText: "Network name")
                    .AutomationName("Network name")
                    .MinWidth(200)
                    .Flex(grow: 1)),
            PickFrom("Current networks", state.Context.Networks, "No networks connected", name => set(c with { Network = name }))
        ) with { RowGap = 8 };

    static Element DeviceControls(DeviceCondition c, SituationState state, Action<Condition> set) =>
        FlexColumn(
            Wrapping(
                ComboBox(["Connected", "Not connected"], c.Not ? 1 : 0, i => set(c with { Not = i == 1 }))
                    .AutomationName("Device test")
                    .MinWidth(150),
                TextBox(c.Device, v => set(c with { Device = v }), placeholderText: "Device name, as Windows shows it")
                    .AutomationName("Device name")
                    .MinWidth(200)
                    .Flex(grow: 1)),
            PickFrom("Connected devices", state.Context.Devices, "No devices connected", name => set(c with { Device = name }))
        ) with { RowGap = 8 };

    static Element BatteryControls(BatteryCondition c, SituationState state, Action<Condition> set)
    {
        bool usesPercent = c.Check is BatteryCheck.Below or BatteryCheck.AtLeast;
        string? now = state.Context.Power switch
        {
            null => null,
            { BatteryPercent: int percent, PluggedIn: var plugged } => $"Now: {percent}%, {(plugged ? "plugged in" : "on battery")}",
            { PluggedIn: true } => "Now: plugged in, no battery",
            _ => "Now: on battery"
        };

        return FlexColumn(
            Wrapping(
                ComboBox(BatteryLabels, (int)c.Check, i => set(c with { Check = (BatteryCheck)Math.Clamp(i, 0, 3), Not = false }))
                    .AutomationName("Battery test")
                    .MinWidth(190),
                usesPercent
                    ? NumberBox(c.Percent, v => set(c with { Percent = double.IsNaN(v) ? c.Percent : Math.Clamp((int)Math.Round(v), 1, 100) }))
                        .Range(1, 100)
                        .SpinButtons(Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact)
                        .AutomationName("Battery percent")
                        .Width(110)
                    : null,
                usesPercent ? Body("%") : null),
            now is null ? null : Caption(now).Foreground(Theme.SecondaryText)
        ) with { RowGap = 8 };
    }

    static Element PlaceControls(PlaceCondition c, SituationState state, Action<Condition> set, Locate locate, Action useCurrent)
    {
        int radiusIndex = Array.IndexOf(PlaceCondition.RadiusChoices, c.RadiusMeters);
        bool denied = locate == Locate.Denied || state.LocationAccess == LocationAccess.Denied;
        string locateLabel = locate == Locate.Finding ? "Finding you…" : c.Center is null ? "Use my current location" : "Move it to where I am";
        string? distance = c.Center is { } center && state.Context.Location is { } here
            ? $"You're {SituationText.Distance(SituationPolicy.DistanceMeters(center, here))} from here."
            : null;

        return FlexColumn(
            Wrapping(
                ComboBox(["Within", "Farther than"], c.Not ? 1 : 0, i => set(c with { Not = i == 1 }))
                    .AutomationName("Place test")
                    .MinWidth(140),
                ComboBox(RadiusLabels, radiusIndex >= 0 ? radiusIndex : Array.IndexOf(PlaceCondition.RadiusChoices, PlaceCondition.DefaultRadius),
                        i => set(c with { RadiusMeters = PlaceCondition.RadiusChoices[Math.Clamp(i, 0, PlaceCondition.RadiusChoices.Length - 1)] }))
                    .AutomationName("Distance")
                    .MinWidth(100),
                Body(c.Not ? "from" : "of"),
                TextBox(c.Place, v => set(c with { Place = v }), placeholderText: "Name, like Home")
                    .AutomationName("Place name")
                    .MinWidth(160)
                    .Flex(grow: 1)),
            Wrapping(
                Button(locateLabel, useCurrent)
                    .IsEnabled(locate != Locate.Finding)
                    .AutomationName(locateLabel)
                    .AutomationId("UseCurrentLocation"),
                Caption(c.Center is { } point ? SituationText.Coordinates(point) : "No place set yet")
                    .Foreground(Theme.SecondaryText),
                distance is null ? null : Caption(distance).Foreground(Theme.SecondaryText)),
            denied
                ? FlexColumn(
                    Caption("Location is off for traydog. Turn it on in Settings > Privacy & security > Location, then try again.")
                        .TextWrapping(TextWrapping.WrapWholeWords),
                    HyperlinkButton("Open location settings", new Uri("ms-settings:privacy-location"))
                ) with { RowGap = 4 }
                : Caption(locate == Locate.Failed
                        ? "Windows couldn't find your location just now. Try again in a moment."
                        : "traydog checks where you are every 10 minutes, and when your network changes, while a situation uses a place.")
                    .Foreground(Theme.SecondaryText)
                    .TextWrapping(TextWrapping.WrapWholeWords)
        ) with { RowGap = 8 };
    }

    enum Locate
    {
        Idle,
        Finding,
        Denied,
        Failed
    }

    static Element TimeControls(TimeCondition c, Action<Condition> set)
    {
        var days = SituationText.WeekOrder.Select(day =>
        {
            var flag = TimeCondition.DayFlag(day);
            bool on = c.Days.HasFlag(flag);
            return ToggleButton(SituationText.ShortDay(day), on, isOn => set(c with { Days = isOn ? c.Days | flag : c.Days & ~flag }))
                .AutomationName(day.ToString())
                .MinWidth(48)
                .WithKey(day.ToString());
        }).ToArray();

        return FlexColumn(
            ComboBox(["Between these times", "Outside these times"], c.Not ? 1 : 0, i => set(c with { Not = i == 1 }))
                .AutomationName("Time test")
                .Flex(alignSelf: FlexAlign.FlexStart),
            Wrapping(
                (TimePicker(c.From.ToTimeSpan(), t => set(c with { From = TimeOnly.FromTimeSpan(t) })) with { Header = "From", MinuteIncrement = 5 })
                    .AutomationName("From"),
                (TimePicker(c.To.ToTimeSpan(), t => set(c with { To = TimeOnly.FromTimeSpan(t) })) with { Header = "To", MinuteIncrement = 5 })
                    .AutomationName("To")) with { AlignItems = FlexAlign.FlexEnd, ColumnGap = 16 },
            Wrapping(days) with { ColumnGap = 4 },
            c.To <= c.From
                ? Caption(c.To == c.From ? "Runs for 24 hours from the start time." : "Ends the next morning; the days are when it starts.")
                    .Foreground(Theme.SecondaryText)
                : null
        ) with { RowGap = 8 };
    }

    // A drop-down of what's connected right now, to fill in a name without typing it.
    static Element PickFrom(string label, IReadOnlyCollection<string>? names, string none, Action<string> pick)
    {
        MenuFlyoutItemBase[] items = names is { Count: > 0 }
            ? names.Select(n => (MenuFlyoutItemBase)MenuItem(n, () => pick(n))).ToArray()
            : [MenuItem(names is null ? "Looking…" : none) with { IsEnabled = false }];
        return DropDownButton(label, MenuItems(items)).Flex(alignSelf: FlexAlign.FlexStart);
    }

    static FlexElement Wrapping(params Element?[] children) =>
        FlexRow(children!) with { ColumnGap = 8, RowGap = 8, AlignItems = FlexAlign.Center, Wrap = FlexWrap.Wrap };
}
