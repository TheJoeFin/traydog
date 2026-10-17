using System;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

class ResourceAlertsContent : Component
{
    public override Element Render()
    {
        var state = UseExternalStore(ResourceMonitor.Subscribe, () => ResourceMonitor.State);
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

        string notice = state.StorageError is { } storageError ? $"Settings not saved: {storageError}"
            : state.Error ?? (state.Overflow > 0
                ? $"Alert limit reached: {state.Overflow} additional alert(s) were not listed."
                : "New alerts appear below · Measurements at detection");

        var commands = CommandBar(
            primaryCommands:
            [
                AppBarButton("Select all", () => setSelected([.. state.Alerts.Select(a => a.Usage.Key)]), "SelectAll")
                    with { IsEnabled = state.Alerts.Count > 0 },
                AppBarButton("Dismiss", () => Apply(ResourceAlertAction.Dismiss), "Clear")
                    with { IsEnabled = targets.Length > 0, Description = "Remind after 15 minutes if usage is still high" },
                AppBarButton("Ignore", () => Apply(ResourceAlertAction.IgnoreOnce), "Cancel")
                    with { IsEnabled = targets.Length > 0, Description = "Mute selected process instances until they exit" }
            ],
            secondaryCommands:
            [
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
                Name(bar.SecondaryCommands[0], "IgnoreAlwaysSelectedResourceAlerts", "Ignore selected executable names every time");
                Name(bar.SecondaryCommands[1], "ClearResourceSelection", "Clear alert selection");
            }).AutomationId("ResourceAlertActions");

        return FlexColumn(
            Border(FlexRow(
                Body("Process alerts").Flex(grow: 1, basis: 0),
                Caption($"{state.Alerts.Count} pending · {targets.Length} selected")
                    .Foreground(Theme.SecondaryText).AutomationId("ResourceAlertCount").LiveRegion().Flex(shrink: 0)
            ) with { ColumnGap = 8, AlignItems = FlexAlign.Center }).Padding(20, 0, 20, 0),
            commands,
            ListView(state.Alerts, a => Id(a.Usage.Key),
                    (alert, _) => Row(alert, selected.Contains(alert.Usage.Key), on => Select(alert.Usage.Key, on))
                        .WithKey(Id(alert.Usage.Key)))
                .Set(list =>
                {
                    list.SelectionMode = ListViewSelectionMode.None;
                    Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Visible);
                })
                .AutomationName("Pending process alerts, oldest first")
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
                    ? "The first 100 pending alerts are kept in place. Additional arrivals are counted, not substituted for rows you may be using."
                    : "Dismiss allows reminders after 15 minutes. Ignore mutes an instance until it exits.")
                .AutomationId("ResourceAlertNotice").LiveRegion().Margin(20, 0, 20, 12)
        ) with { RowGap = 4 };
    }

    static Element Row(ResourceAlert alert, bool selected, Action<bool> select)
    {
        var usage = alert.Usage;
        string id = Id(usage.Key);
        string identity = $"{usage.Name}.exe, PID {usage.Key.Pid}";
        string detection = $"{ResourceText.Kinds(alert.Kinds)} · {alert.At:t}";
        string explanation = alert.Kinds.HasFlag(ResourceKind.OnSight)
            ? "Matches an alert-on-sight rule."
            : $"Usage stayed high for at least {alert.SustainedSeconds} seconds.";

        return Border(FlexColumn(
            FlexRow(
                CheckBox(selected, select).AutomationName($"Select {identity}").AutomationId($"SelectAlert-{id}")
                    .MinWidth(0).Width(32).Flex(shrink: 0),
                (FlexColumn(
                    Body($"{usage.Name}.exe").TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
                        .ToolTip(identity),
                    Caption($"PID {usage.Key.Pid} · {detection}").Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
                        .ToolTip($"{identity}. {detection}. {explanation}")
                ) with { RowGap = 2 }).Flex(grow: 1, shrink: 1, basis: 0)
            ) with { ColumnGap = 8, AlignItems = FlexAlign.Center },
            Caption($"CPU {ResourceText.Cpu(usage.CpuPercent)}   ·   RAM {(usage.RamBytes == 0 && alert.Kinds.HasFlag(ResourceKind.OnSight) ? "Unavailable" : ResourceText.Memory(usage.RamBytes))}")
                .Foreground(Theme.SecondaryText),
            Caption($"Disk {ResourceText.Rate(usage.DiskBytesPerSecond)}   ·   Network {ResourceText.Rate(usage.NetworkBytesPerSecond)}")
                .Foreground(Theme.SecondaryText),
            FlexRow(
                Button("Dismiss", () => ResourceMonitor.Dismiss(alert))
                    .AutomationName($"Dismiss {identity}").AutomationId($"DismissAlert-{id}")
                    .ToolTip("Remind after 15 minutes if usage is still high"),
                MenuFlyout(Button("More actions").AutomationName($"More actions for {identity}").AutomationId($"AlertActions-{id}"),
                [
                    MenuItem("Search online", () => _ = ResourceMonitor.SearchAsync(alert), "Find"),
                    MenuSeparator(),
                    MenuItem("Ignore this time", () => ResourceMonitor.IgnoreOnce(alert)),
                    MenuItem("Ignore every time", () => ResourceMonitor.IgnoreAlways(alert))
                ])
            ) with { ColumnGap = 8 }
        ) with { RowGap = 4 }).Padding(12, 8, 12, 8).AutomationId($"Alert-{id}");
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
