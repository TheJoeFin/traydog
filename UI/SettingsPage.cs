using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // Component, Element
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;                 // TextWrapping
using Microsoft.UI.Xaml.Media.Animation; // EntranceThemeTransition
using Windows.ApplicationModel;          // StartupTaskState
using static Microsoft.UI.Reactor.Factories;

// Settings, swapped into the popout in place of the status page: a couple of inline settings
// don't earn a window of their own. Changes apply immediately; there's no Save button.
class SettingsPage : PopoutContent
{
    static readonly string[] IntervalLabels = CheckInterval.InTrayChoices.Select(CheckInterval.Label).ToArray();

    public override Element Render()
    {
        var popout = UsePopout();
        var backButton = UseFocusWhenShown(popout);
        var (startup, setStartupEnabled) = UseStartupTask(popout.TimesShown);
        var (intervalIndex, setIntervalIndex) = UseState(CheckInterval.IndexOf(Watchdog.InTrayInterval));

        void SelectInterval(int index)
        {
            if (index < 0 || index >= CheckInterval.InTrayChoices.Length)
                return;

            setIntervalIndex(index);
            Watchdog.SetInTrayInterval(CheckInterval.InTrayChoices[index]);
        }

        var content = FlexColumn(
            FlexRow(
                IconButton("", "Back", () => PopoutStore.Navigate(PopoutPage.Status)).Ref(backButton),
                Subtitle("Settings")
            ) with { ColumnGap = 8, AlignItems = FlexAlign.Center },
            StartupCard(startup, setStartupEnabled),
            Card(
                FlexColumn(
                    Body("Check in the background"),
                    Caption("How often traydog checks your connection while this popout is closed.")
                        .Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.WrapWholeWords),
                    // Under its label, not beside it: a ComboBox doesn't fit next to a two-line caption.
                    ComboBox(IntervalLabels, intervalIndex, SelectInterval)
                        .AutomationName("Check in the background")
                        .MinWidth(200)
                ) with { RowGap = 8 }
            ),
            Button("Process monitoring settings", TrayApp.ShowResources).AutomationId("ResourceSettings"),
            Caption(Version).Foreground(Theme.TertiaryText)
        ) with { RowGap = 12 };

        // Scrolls only when text scaling makes it outgrow the popout. Slides in like a drill-in.
        return ScrollView(Border(content).Padding(16, 12, 16, 20))
            .WithTransitions(new EntranceThemeTransition { FromHorizontalOffset = 24, FromVerticalOffset = 0 });
    }

    // "Start with Windows" card. Hidden when unpackaged; locked, with the reason, when the user
    // turned it off in Windows Settings or a policy decides.
    static Element? StartupCard(StartupTaskState? state, Action<bool> setEnabled)
    {
        if (state is not { } s)
            return null;

        string? note = s switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup.",
            StartupTaskState.DisabledByPolicy => "Turned off by your organization.",
            StartupTaskState.EnabledByPolicy => "Turned on by your organization.",
            _ => "Open traydog in the tray when you sign in."
        };

        return Card(
            FlexRow(
                (FlexColumn(
                    Body("Start with Windows"),
                    Caption(note).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.WrapWholeWords)
                ) with { RowGap = 4 }).Flex(grow: 1, shrink: 1, basis: 0),
                ToggleSwitch(s is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy, setEnabled, onContent: "", offContent: "")
                    .MinWidth(0)
                    .IsEnabled(s is StartupTaskState.Enabled or StartupTaskState.Disabled)
                    .AutomationName("Start with Windows")
                    .Flex(shrink: 0)
            ) with { ColumnGap = 12, AlignItems = FlexAlign.Center });
    }

    // Start-with-Windows state, re-read each time the popout opens since it can change in Windows
    // Settings while traydog runs. Null when unpackaged (no toggle) or not loaded yet.
    (StartupTaskState? state, Action<bool> setEnabled) UseStartupTask(int timesShown)
    {
        var (state, setState) = UseState<StartupTaskState?>(null);

        UseEffect(() =>
        {
            _ = Refresh();

            async Task Refresh() => setState(await StartupService.GetStateAsync());
        }, timesShown);

        void SetEnabled(bool enabled)
        {
            _ = Apply();

            async Task Apply() => setState(await StartupService.SetEnabledAsync(enabled));
        }

        return (state, SetEnabled);
    }
}
