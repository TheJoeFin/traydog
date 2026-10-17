using System;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // Component, Element
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;                 // TextWrapping
using static Microsoft.UI.Reactor.Factories;

// The popout's main page: connection status, the monitoring toggle (and which situation, if any,
// set it), a way into situations, and the countdown bar.
class StatusPage : PopoutContent
{
    public override Element Render()
    {
        var state = UseExternalStore(Watchdog.Subscribe, () => Watchdog.State);
        var internet = UseExternalStore(Monitors.Subscribe, () => Monitors.Switch(InternetMonitor.MonitorId));
        var situations = UseExternalStore(SituationEngine.Subscribe, () => SituationEngine.State);
        string? reason = SituationText.Reason(internet, situations.Find);
        var popout = UsePopout();
        var actionButton = UseFocusWhenShown(popout);
        string actionLabel = state.Monitoring ? "Stop monitoring" : "Start monitoring";

        var body = FlexColumn(
            FlexRow(
                Image("ms-appx:///Assets/Dog-face.png").Width(60).Height(48).AutomationName("traydog logo"),
                (FlexColumn(
                    Heading(StatusText.AppName),
                    Caption("Your internet companion").Foreground(Theme.SecondaryText)
                ) with { RowGap = 4 }).Flex(grow: 1, basis: 0),
                IconButton("", "Settings", () => PopoutStore.Navigate(PopoutPage.Settings))
            ) with { ColumnGap = 12, AlignItems = FlexAlign.Center },
            Card(
                FlexColumn(
                    Caption("CONNECTION STATUS").Foreground(Theme.SecondaryText),
                    SubHeading(StatusText.Headline(state.Monitoring, state.Net.Status)).LiveRegion(),
                    Body(StatusText.Description(state.Monitoring, state.Net))
                        .Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.WrapWholeWords)
                ) with { RowGap = 8 }
            ),
            FlexColumn(
                Button(actionLabel, () => Monitors.Choose(InternetMonitor.MonitorId, !state.Monitoring))
                    .AccentButton()
                    .AutomationName(actionLabel)
                    .AutomationId("ToggleMonitoring") // stable selector for UI automation
                    .Ref(actionButton),
                Caption(StatusText.Cadence(state.Monitoring, Watchdog.WhileOpenInterval, Watchdog.InTrayInterval))
                    .Foreground(Theme.SecondaryText)
                    .TextWrapping(TextWrapping.WrapWholeWords),
                reason is null ? null : Caption(reason).TextWrapping(TextWrapping.WrapWholeWords).LiveRegion()
            ) with { RowGap = 8 },
            SituationsLink(situations)
        ) with { RowGap = 16 };

        return FlexColumn(
            Border(body).Padding(20).Flex(grow: 1, basis: 0),
            Component<RefreshBar, RefreshBarProps>(new(state.Monitoring, state.LastProbeAt, state.NextProbeAt, popout.IsVisible)));
    }

    // "Situations · At work", or an invitation when there are none; opens the editor.
    static Element SituationsLink(SituationState situations)
    {
        var active = situations.Situations.Where(s => situations.IsActive(s.Id)).Select(SituationText.Name).ToList();
        string label =
            situations.Situations.Count == 0 ? "Set up situations" :
            active.Count == 0 ? "Situations · none active" :
            $"Situations · {string.Join(", ", active)}";

        return Button(
                FlexRow(
                    Icon(FontIcon("", fontSize: 14)),
                    Caption(label).TextWrapping(TextWrapping.NoWrap)
                ) with { ColumnGap = 8, AlignItems = FlexAlign.Center },
                TrayApp.ShowSituations)
            .SubtleButton()
            .AutomationName($"{label}. Open the situations editor")
            .AutomationId("OpenSituations")
            .ToolTip("Turn monitoring on and off automatically by network, place, power, devices, or time")
            .Margin(-11, 0, 0, 0) // line the text up with the content above
            .Flex(alignSelf: FlexAlign.FlexStart);
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
