using System;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // BackdropKind, Component
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;                 // TextWrapping
using static Microsoft.UI.Reactor.Factories;

// Root Reactor component: renders the tray popout and wires up the custom hooks that drive
// the tray icon (UseTrayPopout), connectivity probing (UseConnectivity), and the refresh
// countdown bar (UseRefreshProgress). Those hooks live in the App.*.cs partial files.
partial class App : Component
{
    public override Element Render()
    {
        var (monitoring, setMonitoring) = UseState(false);
        var (net, setNet) = UseState(new NetState(NetStatus.Unknown, "Not monitoring"));
        var (nextRefreshAt, setNextRefreshAt) = UseState<DateTimeOffset?>(null);
        var (refreshProgress, setRefreshProgress) = UseState(0d);
        var (popoutVisible, setPopoutVisible) = UseState(false);

        UseTrayPopout(monitoring, setMonitoring, net, setPopoutVisible);
        UseConnectivity(monitoring, setNet, setNextRefreshAt);
        UseRefreshProgress(monitoring, popoutVisible, nextRefreshAt, setRefreshProgress);

        string statusHeadline = StatusHeadline(monitoring, net.Status);
        string statusDescription = StatusDescription(monitoring, net);
        string actionLabel = monitoring ? "Stop monitoring" : "Start monitoring";

        // Frameless popout — no TitleBar; the window chrome is stripped in UseTrayPopout.
        var body = Border(
            FlexColumn(
                FlexRow(
                    Image("ms-appx:///Assets/Dog-face.png").Width(60).Height(48).AutomationName("traydog logo"),
                    FlexColumn(
                        Heading("traydog"),
                        Caption("Your internet companion").Foreground(Theme.SecondaryText)
                    ) with { RowGap = 4 }
                ) with { ColumnGap = 12, AlignItems = FlexAlign.Center },
                Card(
                    FlexColumn(
                        Caption("CONNECTION STATUS").Foreground(Theme.SecondaryText),
                        SubHeading(statusHeadline),
                        Body(statusDescription).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.WrapWholeWords)
                    ) with { RowGap = 8 }
                ),
                FlexColumn(
                    Button(actionLabel, () => setMonitoring(!monitoring))
                        .AccentButton()
                        .AutomationName("ToggleMonitoring"),
                    Caption(monitoring
                        ? $"Checking every {(int)ConnectivityMonitor.RefreshInterval.TotalSeconds} seconds"
                        : "Start monitoring to keep an eye on your connection")
                        .Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.WrapWholeWords)
                ) with { RowGap = 8 }
            ) with { RowGap = 16, JustifyContent = FlexJustify.SpaceBetween }
        ).Padding(20).Flex(grow: 1, basis: 0);

        var overlayRadius = ThemeResource.CornerRadius("OverlayCornerRadius");
        return Border(
                FlexColumn(
                    body,
                    RefreshBar(monitoring, nextRefreshAt, refreshProgress).Height(4)))
            .CornerRadius(overlayRadius.TopLeft)
            .WithBorder(Theme.CardStroke, 1)
            .Backdrop(BackdropKind.Mica);
    }

    // Popout headline for the current monitoring/connection state.
    static string StatusHeadline(bool monitoring, NetStatus s) =>
        !monitoring ? "Monitoring paused" :
        s switch
        {
            NetStatus.Online => "✅ You're online",
            NetStatus.NoInternet => "⚠️ Internet unavailable",
            NetStatus.Offline => "🚫 No network connection",
            _ => "🔄️ Checking connection"
        };

    static string StatusDescription(bool monitoring, NetState net) =>
        !monitoring
            ? "Start monitoring to see your connection health here."
            : net.Detail;

    static Element RefreshBar(bool monitoring, DateTimeOffset? nextRefreshAt, double progress) =>
        monitoring && nextRefreshAt is null
            ? ProgressIndeterminate().AutomationName("Scheduling next refresh")
            : Progress(monitoring ? progress : 0).AutomationName("Time until next refresh");
}
