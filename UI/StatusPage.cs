using System;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // Component, Element
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;                 // TextWrapping
using Microsoft.UI.Xaml.Controls.Primitives;
using static Microsoft.UI.Reactor.Factories;

// Monitoring summaries keep their own scroll region so text/status changes never move alert actions.
class StatusPage : PopoutContent
{
    public override Element Render()
    {
        var state = UseExternalStore(Watchdog.Subscribe, () => Watchdog.State);
        var situations = UseExternalStore(SituationEngine.Subscribe, () => SituationEngine.State);
        var resources = UseExternalStore(ResourceMonitor.Subscribe, () => ResourceMonitor.State);
        var popout = UsePopout();
        var settingsButton = UseFocusWhenShown(popout);

        // Keep flyout content in the reconcile path so its store subscriptions stay live.
        var body = FlexColumn(
            FlexRow(
                Image("ms-appx:///Assets/Dog-face.png").Width(48).Height(40).AutomationName("traydog logo"),
                Subtitle(StatusText.AppName).Flex(grow: 1, basis: 0),
                IconButton("", "Situations", TrayApp.ShowSituations).AutomationId("OpenSituations")
                    .ToolTip(SituationsLabel(situations)),
                IconButton("", "Settings", () => PopoutStore.Navigate(PopoutPage.Settings)).AutomationId("OpenSettings").Ref(settingsButton)
            ) with { ColumnGap = 12, AlignItems = FlexAlign.Center },
            Card(FlexRow(
                (FlexColumn(
                    Body("Internet"),
                    Summary(StatusText.Headline(state.Monitoring, state.Net.Status))
                        .AutomationId("ConnectionStatus")
                ) with { RowGap = 2 }).Flex(grow: 1, basis: 0),
                (Flyout(
                    IconButton("\uE946", "Internet monitoring details").Width(32).AutomationId("InternetMonitorInfo"),
                    Component<InternetMonitorDetails>()) with { Placement = FlyoutPlacementMode.Bottom }).Flex(shrink: 0),
                ToggleSwitch(state.Monitoring, on => Monitors.Choose(InternetMonitor.MonitorId, on), onContent: "", offContent: "")
                    .MinWidth(0).AutomationName("Internet monitoring").AutomationId("ToggleMonitoring").Flex(shrink: 0)
            ) with { ColumnGap = 8, AlignItems = FlexAlign.Center })
                .Padding(12, 8, 12, 8).AutomationId("InternetMonitorSummary"),
            Card(FlexRow(
                (FlexColumn(
                    Body("Processes"),
                    Summary(resources.Error is not null || resources.StorageError is not null ? "Needs attention"
                        : resources.Connecting ? "Connecting collector..."
                        : resources.Running ? "Monitoring" : "Paused").AutomationId("ProcessMonitoringStatus")
                ) with { RowGap = 2 }).Flex(grow: 1, basis: 0),
                (Flyout(
                    IconButton("\uE946", "Process monitoring details").Width(32).AutomationId("ProcessMonitorInfo"),
                    Component<ProcessMonitorDetails>()) with { Placement = FlyoutPlacementMode.Bottom }).Flex(shrink: 0),
                IconButton("", "Process monitoring settings", TrayApp.ShowResources).AutomationId("OpenResources").Flex(shrink: 0),
                ToggleSwitch(resources.Running, on => Monitors.Choose(ResourceMonitor.MonitorId, on), onContent: "", offContent: "")
                    .MinWidth(0).AutomationName("Process monitoring").AutomationId("ToggleProcessMonitoring").Flex(shrink: 0)
            ) with { ColumnGap = 8, AlignItems = FlexAlign.Center })
                .Padding(12, 8, 12, 8).AutomationId("ProcessMonitorSummary")
        ) with { RowGap = 8 };

        return FlexColumn(
            ScrollView(Border(body).Padding(20, 16, 20, 8)).Height(MonitoringLayout.SummaryHeight)
                .AutomationId("MonitoringSummary").Flex(shrink: 0),
            Component<ResourceAlertsContent>().Flex(grow: 1, basis: 0),
            Component<RefreshBar, RefreshBarProps>(new(state.Monitoring, state.LastProbeAt, state.NextProbeAt, popout.IsVisible)));
    }

    static Microsoft.UI.Reactor.Core.TextBlockElement Summary(string text) =>
        Caption(text).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.NoWrap)
            .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(text);

    static string SituationsLabel(SituationState situations)
    {
        var active = situations.Situations.Where(s => situations.IsActive(s.Id)).Select(SituationText.Name).ToList();
        return
            situations.Situations.Count == 0 ? "Set up situations" :
            active.Count == 0 ? "Situations · none active" :
            $"Situations · {string.Join(", ", active)}";
    }
}

class InternetMonitorDetails : Component
{
    public override Element Render()
    {
        var state = UseExternalStore(Watchdog.Subscribe, () => Watchdog.State);
        var monitor = UseExternalStore(Monitors.Subscribe, () => Monitors.Switch(InternetMonitor.MonitorId));
        var situations = UseExternalStore(SituationEngine.Subscribe, () => SituationEngine.State);
        string? reason = SituationText.Reason(monitor, situations.Find);

        return (FlexColumn(
            BodyStrong("Internet monitoring"),
            ResourcesPage.Note(StatusText.Description(state.Monitoring, state.Net)),
            state.Monitoring ? ResourcesPage.Note(StatusText.Cadence(true, Watchdog.WhileOpenInterval, Watchdog.InTrayInterval)) : null,
            reason is null ? null : ResourcesPage.Note(reason).AutomationId("InternetSituationReason"),
            HyperlinkButton("Manage situations", onClick: TrayApp.ShowSituations).AutomationId("ManageInternetSituations")
        ) with { RowGap = 8 }).Width(300).AutomationId("InternetMonitorDetails");
    }
}

class ProcessMonitorDetails : Component
{
    public override Element Render()
    {
        var state = UseExternalStore(ResourceMonitor.Subscribe, () => ResourceMonitor.State);
        var monitor = UseExternalStore(Monitors.Subscribe, () => Monitors.Switch(ResourceMonitor.MonitorId));
        var situations = UseExternalStore(SituationEngine.Subscribe, () => SituationEngine.State);
        string? reason = SituationText.Reason(monitor, situations.Find);

        return (FlexColumn(
            BodyStrong("Process monitoring"),
            ResourcesPage.Note(ResourceText.Coverage(state.Options, state.IoConnected)),
            ResourcesPage.Note(state.Running
                ? $"Scanning every {state.Options.ScanSeconds}s. Suspects, alerts, and watched apps rechecked every {state.Options.ConfirmSeconds}s."
                : "No process scans or resource tracing run in the background."),
            state.Running ? ResourcesPage.Note($"{state.Candidates} suspect process(es). {state.Unavailable} protected or exited process(es) could not be read. Last scan: {(state.LastScan is { } last ? last.ToString("T") : "not yet")}.") : null,
            state.Error is null ? null : InfoBar("Monitoring notice", state.Error).Warning().IsClosable(false),
            state.StorageError is null ? null : InfoBar("Settings not saved", state.StorageError).Warning().IsClosable(false),
            reason is null ? null : ResourcesPage.Note(reason).AutomationId("ProcessSituationReason"),
            HyperlinkButton("Manage situations", onClick: TrayApp.ShowSituations).AutomationId("ManageProcessSituations")
        ) with { RowGap = 8 }).Width(300).AutomationId("ProcessMonitorDetails");
    }
}

record RefreshBarProps(bool Monitoring, DateTimeOffset? LastProbeAt, DateTimeOffset? NextProbeAt, bool IsVisible);

// The 4 px countdown along the bottom edge. Its own component so the once-a-second tick re-renders
// only the bar, and it ticks only while the popout is on screen.
class RefreshBar : Component<RefreshBarProps>
{
    public override Element Render()
    {
        var now = UseClock(Props.Monitoring && Props.IsVisible);

        if (!Props.Monitoring)
            return Progress(0).AutomationName("Time until next check").Height(4);

        return StatusText.ProgressPercent(Props.LastProbeAt, Props.NextProbeAt, now) is { } percent
            ? Progress(percent).AutomationName("Time until next check").Height(4)
            : ProgressIndeterminate().AutomationName("Checking connection").Height(4);
    }

    // The current time, refreshed every second while running.
    DateTimeOffset UseClock(bool running)
    {
        var (now, setNow) = UseState(DateTimeOffset.UtcNow);
        UseEffect(() =>
        {
            if (!running)
                return () => { };

            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (_, _) => setNow(DateTimeOffset.UtcNow);
            setNow(DateTimeOffset.UtcNow);
            timer.Start();
            return () => timer.Stop();
        }, running);
        return now;
    }
}
