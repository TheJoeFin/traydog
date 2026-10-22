using System;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

class ResourceAlertsContent : Component
{
    // Reactor's ThemeBindings supports Foreground/Background/BorderBrush, not shape Stroke.
    static readonly Style ChartStyle = (Style)XamlReader.Load("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Path">
            <Setter Property="Stroke" Value="{ThemeResource TextFillColorPrimaryBrush}" />
        </Style>
        """);

    public override Element Render()
    {
        var state = UseExternalStore(ResourceMonitor.Subscribe, () => ResourceMonitor.State);
        var dismiss = UseContext(TrayPopout.Dismiss);
        var (selected, setSelected) = UseState(Array.Empty<ProcessKey>());
        var targets = ResourceAlertList.Selected(state.Alerts, selected);

        UseEffect(() =>
        {
            if (targets.Length != selected.Length)
                setSelected([.. targets.Select(a => a.Usage.Key)]);
        }, state.Alerts, selected);

        void Select(ProcessKey key, bool on) => setSelected(on
            ? [.. selected.Where(k => k != key), key]
            : [.. selected.Where(k => k != key)]);

        void Apply(ResourceAlertAction action) => ResourceMonitor.ApplyAlerts(targets, action);

        void Watch(ResourceAlert[] alerts)
        {
            ResourceMonitor.ApplyAlerts(alerts, ResourceAlertAction.WatchAndDismiss);
            setSelected([]);
            dismiss();
        }

        string notice = state.StorageError is { } storageError ? $"Settings not saved: {storageError}"
            : state.Error ?? (state.Overflow > 0
                ? $"Alert limit reached: {state.Overflow} additional alert(s) were not listed."
                : state.Running ? $"Live checks every {state.Options.ConfirmSeconds}s · Up to {ResourceHistory.Limit} bars per app"
                    : "Monitoring paused · History kept for this session");

        // Live samples must not rebuild an open overflow menu.
        var commands = UseMemo(() => CommandBar(
            primaryCommands:
            [
                AppBarButton("Select all", () => setSelected([.. ResourceMonitor.State.Alerts.Select(a => a.Usage.Key)]), "SelectAll")
                    with { IsEnabled = state.Alerts.Count > 0 },
                AppBarButton("Dismiss", () => Apply(ResourceAlertAction.Dismiss), "Clear")
                    with { IsEnabled = targets.Length > 0, Description = "Remove selected alerts or watches; allow reminders after 15 minutes" },
                AppBarButton("Ignore", () => Apply(ResourceAlertAction.IgnoreOnce), "Cancel")
                    with { IsEnabled = targets.Length > 0, Description = "Mute selected process instances until they exit" }
            ],
            secondaryCommands:
            [
                AppBarButton("Watch and dismiss", () => Watch(targets), "View")
                    with { IsEnabled = targets.Any(a => a.CanWatch), Description = "Keep recording selected apps without reopening the popup" },
                AppBarButton("Ignore every time", () => Apply(ResourceAlertAction.IgnoreAlways))
                    with { IsEnabled = targets.Length > 0, Description = "Save executable-name rules for the selected processes" },
                AppBarButton("Clear selection", () => setSelected([])) with { IsEnabled = targets.Length > 0 }
            ]).Set(bar =>
            {
                bar.DefaultLabelPosition = CommandBarDefaultLabelPosition.Right;
                bar.IsDynamicOverflowEnabled = false;
                Name(bar.PrimaryCommands[0], "SelectAllResourceAlerts", "Select all current alerts");
                Name(bar.PrimaryCommands[1], "DismissSelectedResourceAlerts", "Dismiss selected alerts");
                Name(bar.PrimaryCommands[2], "IgnoreSelectedResourceAlerts", "Ignore selected process instances");
                Name(bar.SecondaryCommands[0], "WatchSelectedResourceAlerts", "Watch and dismiss selected alerts");
                Name(bar.SecondaryCommands[1], "IgnoreAlwaysSelectedResourceAlerts", "Ignore selected executable names every time");
                Name(bar.SecondaryCommands[2], "ClearResourceSelection", "Clear alert selection");
            }).AutomationId("ResourceAlertActions"), selected, state.Alerts.Count, targets.Any(a => a.CanWatch));

        return FlexColumn(
            Border(FlexRow(
                Body("Process alerts").Flex(grow: 1, basis: 0),
                Caption($"{state.PendingCount} pending · {state.WatchingCount} watching{(state.StoppedCount > 0 ? $" · {state.StoppedCount} stopped" : "")} · {targets.Length} selected")
                    .Foreground(Theme.SecondaryText).AutomationId("ResourceAlertCount").LiveRegion().Flex(shrink: 0)
            ) with { ColumnGap = 8, AlignItems = FlexAlign.Center }).Padding(20, 0, 20, 0),
            commands,
            ListView(state.Alerts, a => Id(a.Usage.Key),
                    (alert, _) => Row(alert, state, selected.Contains(alert.Usage.Key),
                        on => Select(alert.Usage.Key, on), () => Watch([alert]))
                        .WithKey(Id(alert.Usage.Key)))
                .Set(list =>
                {
                    list.SelectionMode = ListViewSelectionMode.None;
                    Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Visible);
                })
                .AutomationName("Process alerts and watched apps, oldest first")
                .AutomationId("ResourceAlerts")
                .Flex(grow: 1, basis: 0),
            state.Alerts.Count == 0 ? ResourcesPage.Note(state.Running
                    ? "Nothing to flag. New process alerts will appear here."
                    : "No pending alerts. Turn on process monitoring to watch for high usage.")
                .Margin(20, 0, 20, 8).WithKey("empty") : null,
            Caption(notice)
                .Foreground(state.Overflow > 0 || state.Error is not null || state.StorageError is not null
                    ? Theme.PrimaryText : Theme.SecondaryText)
                .TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
                .ToolTip(state.Error is not null || state.StorageError is not null ? notice : state.Overflow > 0
                    ? "The first 100 alerts and watches are kept in place. Additional arrivals are counted, not substituted for rows you may be using."
                    : "Watch and dismiss keeps recording without reopening the popup. Stop watching removes the record. Histories last only for this traydog session.")
                .AutomationId("ResourceAlertNotice").LiveRegion().Margin(20, 0, 20, 12)
        ) with { RowGap = 4 };
    }

    static Element Row(ResourceAlert alert, ResourceState state, bool selected, Action<bool> select, Action watch)
    {
        var usage = alert.Usage;
        string id = Id(usage.Key);
        string identity = $"{usage.Name}.exe, PID {usage.Key.Pid}";
        string detection = $"{ResourceText.Kinds(alert.Kinds)} · {alert.At:t}";
        string explanation = alert.Kinds.HasFlag(ResourceKind.OnSight)
            ? "Matches an alert-on-sight rule."
            : $"Usage stayed high for at least {alert.SustainedSeconds} seconds.";
        string status = ResourceText.Status(alert, state.Running, state.Options);
        var lastReading = alert.IsStopped
            ? alert.History.LastOrDefault(p => p.Usage is not null) : alert.History.LastOrDefault();
        string lastCheck = lastReading is not null ? $" · {(alert.IsStopped ? "Last recorded " : "")}{lastReading.At:T}" : "";
        var io = state.Options.IoKinds(state.IoConnected);
        string remove = alert.IsStopped ? "Remove" : alert.Watching ? "Stop watching" : "Dismiss";

        return Border(FlexColumn(
            (FlexRow(
                CheckBox(selected, select).AutomationName($"Select {identity}").AutomationId($"SelectAlert-{id}")
                    .MinWidth(0).Width(32).Flex(shrink: 0),
                (FlexColumn(
                    Body($"{usage.Name}.exe").TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
                        .ToolTip(identity),
                    Caption($"{(alert.IsStopped ? "Stopped · " : alert.Watching ? "Watching · " : "")}PID {usage.Key.Pid} · {detection}").Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
                        .ToolTip($"{identity}. {detection}. {explanation}")
                ) with { RowGap = 2 }).Flex(grow: 1, shrink: 1, basis: 0)
            ) with { ColumnGap = 8, AlignItems = FlexAlign.Center }).WithKey("process"),
            (FlexRow(
                Caption(status + lastCheck).Foreground(Theme.SecondaryText).AutomationId($"UsageStatus-{id}")
                    .TextWrapping(alert.IsStopped ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap)
                    .TextTrimming(alert.IsStopped ? TextTrimming.None : TextTrimming.CharacterEllipsis)
                    .ToolTip(status + lastCheck).Flex(grow: 1, basis: 0),
                Caption($"{alert.History.Count} checks").Foreground(Theme.SecondaryText)
                    .ToolTip($"Checks retained in the rolling history, up to {ResourceHistory.Limit}. Unavailable readings leave an empty slot.")
                    .AutomationId($"UsageCheckCount-{id}").Flex(shrink: 0)
            ) with { ColumnGap = 8, AlignItems = FlexAlign.Center }).WithKey("status"),
            (FlexRow(
                UsageBars(alert, ResourceKind.Cpu, id).Flex(grow: 1, basis: 0),
                UsageBars(alert, ResourceKind.Ram, id).Flex(grow: 1, basis: 0)
            ) with { ColumnGap = 12 }).WithKey("usage"),
            io == ResourceKind.None ? null : (FlexRow(
                io.HasFlag(ResourceKind.Disk) ? UsageBars(alert, ResourceKind.Disk, id).Flex(grow: 1, basis: 0) : null,
                io.HasFlag(ResourceKind.Network) ? UsageBars(alert, ResourceKind.Network, id).Flex(grow: 1, basis: 0) : null
            ) with { ColumnGap = 12 }).WithKey("io"),
            (FlexRow(
                Button(remove, () => ResourceMonitor.Dismiss(alert))
                    .AutomationName($"{remove} {identity}").AutomationId($"DismissAlert-{id}")
                    .ToolTip(alert.IsStopped ? "Remove this closed process and its recorded history."
                        : alert.Watching ? "Remove this watch and its history; allow reminders after 15 minutes"
                        : "Remind after 15 minutes if usage is still high"),
                Memo((usage.Key, alert.Watching, alert.IsStopped), () => MenuFlyout(
                    Button("More actions").AutomationName($"More actions for {identity}").AutomationId($"AlertActions-{id}"),
                [
                    MenuItem("Watch and dismiss", watch, "View") with { IsEnabled = alert.CanWatch },
                    MenuSeparator(),
                    MenuItem("Search online", () => _ = ResourceMonitor.SearchAsync(alert), "Find"),
                    MenuSeparator(),
                    MenuItem("Ignore this time", () => ResourceMonitor.IgnoreOnce(alert)),
                    MenuItem("Ignore every time", () => ResourceMonitor.IgnoreAlways(alert))
                ]))
            ) with { ColumnGap = 8 }).WithKey("actions")
        ) with { RowGap = 4 }).Padding(12, 8, 12, 8).AutomationId($"Alert-{id}");
    }

    static Element UsageBars(ResourceAlert alert, ResourceKind kind, string id)
    {
        string label = $"{ResourceText.Kinds(kind)} {ResourceHistory.Format(ResourceHistory.Value(alert.Current, kind), kind)}";
        string trend = ResourceHistory.Trend(alert, kind);
        string range = alert.History.Count > 0
            ? $"{alert.History[0].At:T} to {alert.History[^1].At:T}, {alert.History.Count} retained checks"
            : "Waiting for the first check";
        var bars = Path2D() with
        {
            PathDataString = ResourceHistory.BarPath(alert.History, kind),
            StrokeThickness = ResourceHistory.BarWidth
        };
        return FlexColumn(
            Caption(label).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.NoWrap)
                .TextTrimming(TextTrimming.CharacterEllipsis).AutomationId($"Usage-{kind}-{id}"),
            Viewbox(Canvas([bars.Set(path => path.Style = ChartStyle)]).Width(ResourceHistory.ChartWidth).Height(ResourceHistory.ChartHeight))
                .Stretch(Stretch.Fill).Height(ResourceHistory.ChartHeight)
        ).ToolTip($"{label}. {trend}. {range}. One bar per check, newest on the right. Each chart has its own zero-based scale; CPU uses 0 to 100%. Empty slots mean unavailable data.")
            .AutomationName($"{label}. {trend}. {range}. One bar per check, newest on the right.")
            .AutomationId($"UsageChart-{kind}-{id}").WithKey(kind.ToString());
    }

    static string Id(ProcessKey key) => $"{key.Pid}-{key.StartedUtcTicks}";

    static void Name(ICommandBarElement item, string id, string name)
    {
        if (item is FrameworkElement element)
        {
            AutomationProperties.SetAutomationId(element, id);
            AutomationProperties.SetName(element, name);
            ToolTipService.SetToolTip(element, name);
        }
    }
}
