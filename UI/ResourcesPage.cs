using System;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

class ResourcesPage : Component
{
    public override Element Render()
    {
        var state = UseExternalStore(ResourceMonitor.Subscribe, () => ResourceMonitor.State);
        var monitorSwitch = UseExternalStore(Monitors.Subscribe, () => Monitors.Switch(ResourceMonitor.MonitorId));
        var situations = UseExternalStore(SituationEngine.Subscribe, () => SituationEngine.State);
        var (tab, setTab) = UseState(0);
        string? reason = SituationText.Reason(monitorSwitch, situations.Find);

        Element content = tab switch
        {
            1 => Component<ResourceThresholds>().WithKey("thresholds"),
            2 => Component<ResourceRules>().WithKey("rules"),
            _ => Component<ResourceAlertsContent>().WithKey("alerts")
        };

        return Border(FlexColumn(
            (FlexRow(
                Subtitle("Process monitoring").Flex(grow: 1, basis: 0),
                ToggleSwitch(state.Running, on => Monitors.Choose(ResourceMonitor.MonitorId, on), onContent: "On", offContent: "Off")
                    .AutomationName("Process monitoring")
                    .AutomationId("ToggleProcessMonitoring")
            ) with { ColumnGap = 12, AlignItems = FlexAlign.Center }).WithKey("header"),
            Note(state.Running
                ? $"Scanning every {state.Options.ScanSeconds}s. {state.Candidates} process(es) being checked more closely."
                : "Off. No process scans or resource tracing run in the background.").WithKey("status"),
            reason is null ? null : Note(reason).WithKey("reason"),
            state.StorageError is null ? null : InfoBar("Settings not saved", state.StorageError).Warning().IsClosable(false).WithKey("storage"),
            state.Error is null ? null : InfoBar("Monitoring notice", state.Error).Warning().IsClosable(false).WithKey("error"),
            CollectorCard(state).WithKey("collector"),
            SelectorBar([SelectorBarItem("Alerts"), SelectorBarItem("Thresholds"), SelectorBarItem("Rules")], tab, setTab)
                .AutomationName("Process monitoring sections").AutomationId("ProcessSections").WithKey("tabs"),
            content.Flex(grow: 1, basis: 0)
        ) with { RowGap = 12 }).Padding(24);
    }

    static Element CollectorCard(ResourceState state) => Card(
        FlexColumn(
            FlexRow(
                (FlexColumn(
                    Body(state.IoConnected ? "Disk / Network: connected" : "Disk / Network: permission needed"),
                    Note(state.IoConnected
                        ? "Separate disk and TCP/UDP byte rates, plus immediate process-launch alerts."
                        : "CPU/RAM work without admin. Alert-on-sight uses scans and may miss short-lived processes.")
                ) with { RowGap = 4 }).Flex(grow: 1, shrink: 1, basis: 0),
                Button(state.Connecting ? "Waiting for permission..." : state.IoConnected ? "Disconnect" : "Enable (admin)",
                        () => { if (state.IoConnected) ResourceMonitor.DisableCollector(); else _ = ResourceMonitor.EnableCollectorAsync(); })
                    .IsEnabled(!state.Connecting)
                    .AutomationName(state.IoConnected ? "Disconnect Disk and Network collector" : "Enable Disk and Network collector")
                    .AutomationId("EnableResourceCollector")
                    .Flex(shrink: 0)
            ) with { ColumnGap = 16, AlignItems = FlexAlign.Center },
            Note("The optional collector runs only while process monitoring is on. Permission is requested again each app session.")
        ) with { RowGap = 8 });

    internal static Element Note(string text) => Caption(text).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.WrapWholeWords);
}

class ResourceThresholds : Component
{
    public override Element Render()
    {
        var options = UseExternalStore(ResourceMonitor.Subscribe, () => ResourceMonitor.State.Options);
        void Update(ResourceOptions next) => ResourceMonitor.UpdateOptions(next);

        return ScrollView(FlexColumn(
            Subtitle("Adaptive checks"),
            ResourcesPage.Note("Quiet processes use the scan interval. Suspected problems alone are rechecked quickly. Each resource must stay high for the full confirmation period."),
            NumberBox(options.ScanSeconds, v => { if (double.IsFinite(v)) Update(options with { ScanSeconds = (int)Math.Round(v) }); }, "Background scan (seconds)")
                .Range(5, 3600).SpinButtons().AutomationName("Background scan in seconds").AutomationId("ResourceScanSeconds"),
            NumberBox(options.ConfirmSeconds, v => { if (double.IsFinite(v)) Update(options with { ConfirmSeconds = (int)Math.Round(v) }); }, "Suspect recheck (seconds)")
                .Range(1, 10).SpinButtons().AutomationName("Suspect recheck in seconds").AutomationId("ResourceConfirmSeconds"),
            NumberBox(options.SustainedSeconds, v => { if (double.IsFinite(v)) Update(options with { SustainedSeconds = (int)Math.Round(v) }); }, "Must stay high for (seconds)")
                .Range(5, 300).SpinButtons().AutomationName("Sustained usage in seconds").AutomationId("ResourceSustainedSeconds"),
            Subtitle("Per-process thresholds").Margin(0, 12, 0, 0),
            Threshold("CPU", "Percent of the whole machine, not one core.", options.CpuEnabled, options.CpuPercent, 1, 100, "%",
                on => Update(options with { CpuEnabled = on }), v => Update(options with { CpuPercent = v })),
            Threshold("RAM", "Working set in binary megabytes (1 GB = 1024 MB).", options.RamEnabled, options.RamMegabytes, 1, 1_048_576, "MB",
                on => Update(options with { RamEnabled = on }), v => Update(options with { RamMegabytes = v })),
            Threshold("Disk", "Actual disk reads + writes. Requires the optional collector.", options.DiskEnabled, options.DiskMegabytesPerSecond, 0.1, 1_000_000, "MB/s",
                on => Update(options with { DiskEnabled = on }), v => Update(options with { DiskMegabytesPerSecond = v })),
            Threshold("Network", "TCP + UDP send/receive, IPv4 + IPv6. Requires the optional collector.", options.NetworkEnabled, options.NetworkMegabytesPerSecond, 0.1, 1_000_000, "MB/s",
                on => Update(options with { NetworkEnabled = on }), v => Update(options with { NetworkMegabytesPerSecond = v })),
            ResourcesPage.Note("Disk is physical I/O, not file-cache traffic or disk active time. Network counts transferred bytes, not packet contents. Unavailable measurements never trigger an alert.")
        ) with { RowGap = 12 });
    }

    static Element Threshold(string title, string description, bool enabled, double value, double min, double max,
        string unit, Action<bool> toggle, Action<double> change) => Card(
        FlexRow(
            (FlexColumn(Body(title), ResourcesPage.Note(description)) with { RowGap = 4 }).Flex(grow: 1, shrink: 1, basis: 0),
            NumberBox(value, v => { if (double.IsFinite(v)) change(v); }, unit).Range(min, max)
                .Width(130).IsEnabled(enabled).AutomationName($"{title} threshold in {unit}").AutomationId($"Resource{title}Threshold"),
            ToggleSwitch(enabled, toggle, onContent: "", offContent: "").MinWidth(0)
                .AutomationName($"Watch {title}").AutomationId($"Resource{title}Enabled")
        ) with { ColumnGap = 12, AlignItems = FlexAlign.Center });
}

class ResourceRules : Component
{
    public override Element Render()
    {
        var options = UseExternalStore(ResourceMonitor.Subscribe, () => ResourceMonitor.State.Options);
        var (name, setName) = UseState("");
        var (kind, setKind) = UseState(0);
        var (error, setError) = UseState<string?>(null);
        bool onSight = kind == 0;
        string[] names = onSight ? options.OnSightNames : options.IgnoredNames;

        void Add()
        {
            string? problem = ResourceMonitor.AddRule(name, onSight);
            setError(problem);
            if (problem is null)
                setName("");
        }

        return FlexColumn(
            ResourcesPage.Note("Exact executable names, case-insensitive. Rules cover every executable with that name, regardless of folder. Ignore every time and alert on sight are mutually exclusive."),
            ComboBox(["Alert on sight", "Ignore every time"], kind, setKind).AutomationName("Rule type").AutomationId("ResourceRuleType"),
            FlexRow(
                TextBox(name, setName, placeholderText: "example.exe", header: "Executable name")
                    .AutomationName("Executable name").AutomationId("ResourceRuleName").Flex(grow: 1, basis: 0),
                Button("Add rule", Add).AutomationId("AddResourceRule").Flex(alignSelf: FlexAlign.End)
            ) with { ColumnGap = 12 },
            error is null ? null : InfoBar("Rule not added", error).Warning().IsClosable(false),
            ResourcesPage.Note(onSight
                ? "Alerts once for each new process instance, even with low usage. Also checks matching processes already running when monitoring or a rule is enabled. Enable the collector to catch short launches between scans."
                : "Never alert for these names, including on sight. Remove a rule to allow future alerts again."),
            names.Length == 0 ? ResourcesPage.Note("No rules of this type.") :
                ListView(names.Select(n =>
                    (FlexRow(
                        Body(n + ".exe").Flex(grow: 1, shrink: 1, basis: 0),
                        Button("Remove", () => ResourceMonitor.RemoveRule(n, onSight)).AutomationName($"Remove rule for {n}.exe")
                    ) with { ColumnGap = 12, AlignItems = FlexAlign.Center }).Padding(8).WithKey(n)
                ).ToArray()).SelectionMode(ListViewSelectionMode.None).AutomationId("ResourceRules").Flex(grow: 1, basis: 0)
        ) with { RowGap = 12 };
    }
}
