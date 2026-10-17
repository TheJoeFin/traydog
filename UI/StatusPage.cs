using System;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // Component, Element
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;                 // TextWrapping
using static Microsoft.UI.Reactor.Factories;

// Monitoring summaries keep their own scroll region so text/status changes never move alert actions.
class StatusPage : PopoutContent
{
    public override Element Render()
    {
        var state = UseExternalStore(Watchdog.Subscribe, () => Watchdog.State);
        var internet = UseExternalStore(Monitors.Subscribe, () => Monitors.Switch(InternetMonitor.MonitorId));
        var processes = UseExternalStore(Monitors.Subscribe, () => Monitors.Switch(ResourceMonitor.MonitorId));
        var situations = UseExternalStore(SituationEngine.Subscribe, () => SituationEngine.State);
        var resources = UseExternalStore(ResourceMonitor.Subscribe, () => ResourceMonitor.State);
        string? internetReason = SituationText.Reason(internet, situations.Find);
        string? processReason = SituationText.Reason(processes, situations.Find);
        var popout = UsePopout();
        var settingsButton = UseFocusWhenShown(popout);

        var body = FlexColumn(
            FlexRow(
                Image("ms-appx:///Assets/Dog-face.png").Width(48).Height(40).AutomationName("traydog logo"),
                (FlexColumn(
                    Subtitle(StatusText.AppName),
                    Caption("Your quiet watchdog").Foreground(Theme.SecondaryText)
                ) with { RowGap = 4 }).Flex(grow: 1, basis: 0),
                IconButton("", "Situations", TrayApp.ShowSituations).AutomationId("OpenSituations")
                    .ToolTip(SituationsLabel(situations)),
                IconButton("", "Settings", () => PopoutStore.Navigate(PopoutPage.Settings)).AutomationId("OpenSettings").Ref(settingsButton)
            ) with { ColumnGap = 12, AlignItems = FlexAlign.Center },
            Card(
                FlexColumn(
                    FlexRow(
                        Body("Internet").Flex(grow: 1, basis: 0),
                        ToggleSwitch(state.Monitoring, on => Monitors.Choose(InternetMonitor.MonitorId, on), onContent: "", offContent: "")
                            .MinWidth(0).AutomationName("Internet monitoring").AutomationId("ToggleMonitoring")
                    ) with { ColumnGap = 12, AlignItems = FlexAlign.Center },
                    Summary(StatusText.Headline(state.Monitoring, state.Net.Status))
                        .ToolTip($"{StatusText.Description(state.Monitoring, state.Net)}\n{StatusText.Cadence(state.Monitoring, Watchdog.WhileOpenInterval, Watchdog.InTrayInterval)}")
                        .AutomationId("ConnectionStatus"),
                    state.Monitoring ? Summary(state.Net.Detail) : null,
                    internetReason is null ? null : ResourcesPage.Note(internetReason)
                ) with { RowGap = 4 }
            ),
            Card(FlexColumn(
                FlexRow(
                    Body("Processes").Flex(grow: 1, basis: 0),
                    IconButton("", "Process monitoring settings", TrayApp.ShowResources).AutomationId("OpenResources"),
                    ToggleSwitch(resources.Running, on => Monitors.Choose(ResourceMonitor.MonitorId, on), onContent: "", offContent: "")
                        .MinWidth(0).AutomationName("Process monitoring").AutomationId("ToggleProcessMonitoring")
                ) with { ColumnGap = 12, AlignItems = FlexAlign.Center },
                Summary(resources.Running
                    ? $"{ResourceText.Coverage(resources.Options, resources.IoConnected)} · {resources.Options.ScanSeconds}s scans"
                    : "Off · No background process scans")
                    .ToolTip(resources.Running
                        ? $"{resources.Candidates} process(es) being checked closely. {resources.Unavailable} protected or exited process(es) could not be read. Last scan: {(resources.LastScan is { } last ? last.ToString("T") : "not yet")}."
                        : "No process scans or resource tracing run in the background."),
                processReason is null ? null : ResourcesPage.Note(processReason)
            ) with { RowGap = 4 })
        ) with { RowGap = 8 };

        return FlexColumn(
            ScrollView(Border(body).Padding(20, 16, 20, 8)).Height(MonitoringLayout.SummaryHeight).Flex(shrink: 0),
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
