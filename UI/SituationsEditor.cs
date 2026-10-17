using System;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // Component, Element
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;                 // TextWrapping
using static Microsoft.UI.Reactor.Factories;

// Root of the situations window: the list of situations on the left (in priority order), the
// selected one's editor on the right. Everything reads SituationEngine.State and edits go straight
// back to the engine, which saves and re-evaluates; there's no Save button.
class SituationsEditor : Component
{
    public override Element Render()
    {
        var state = UseExternalStore(SituationEngine.Subscribe, () => SituationEngine.State);
        var (selectedId, setSelectedId) = UseState<string?>(null);
        var select = UseStableSelect(setSelectedId);
        var selected = (selectedId is null ? null : state.Find(selectedId)) ?? state.Situations.FirstOrDefault();

        Element detail = selected is null
            ? EmptyState(() => AddSituation(state, select))
            : Component<SituationEditor, SituationEditorProps>(new(
                    selected,
                    state,
                    IsFirst: state.Situations[0].Id == selected.Id,
                    IsLast: state.Situations[^1].Id == selected.Id))
                .WithKey(selected.Id);

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            TitleBar("Situations").Subtitle(StatusText.AppName).Icon("ms-appx:///Assets/Dog-face.png").Grid(row: 0, column: 0),
            Grid(
                columns: [GridSize.Px(280), GridSize.Star()],
                rows: [GridSize.Star()],
                Component<SituationList, SituationListProps>(new(state, selected?.Id, select)).Grid(row: 0, column: 0),
                Border(detail)
                    .Background(Theme.LayerFill)
                    .BorderBrush(Theme.CardStroke)
                    .BorderThickness(1, 1, 0, 0)
                    .CornerRadius(8, 0, 0, 0)
                    .Grid(row: 0, column: 1)
            ).Grid(row: 1, column: 0));
    }

    // One delegate for the window's lifetime that calls the latest setter, so SituationList's props
    // compare equal across the root re-renders that typing in the editor causes.
    Action<string?> UseStableSelect(Action<string?> setSelectedId)
    {
        var latest = UseRef(setSelectedId);
        latest.Current = setSelectedId;
        return UseMemo(() => new Action<string?>(id => latest.Current!(id)));
    }

    public static void AddSituation(SituationState state, Action<string?> select)
    {
        var situation = Situation.Create(NewName(state));
        if (Monitors.All.FirstOrDefault() is { } first)
            situation = situation.WithAction(first.Id, true);
        SituationEngine.Add(situation);
        select(situation.Id);
    }

    static Element EmptyState(Action add) =>
        FlexColumn(
            Icon(FontIcon("", fontSize: 40)).Foreground(Theme.AccentText),
            Subtitle("Let traydog decide when to watch"),
            Body("A situation is a set of conditions, like being on your office Wi-Fi, docked, on battery, " +
                 "near home, or inside work hours. While it holds, it switches monitors on or off for you. " +
                 "When it ends, they go back to what you chose.")
                .Foreground(Theme.SecondaryText)
                .TextWrapping(TextWrapping.WrapWholeWords)
                .TextAlignment(TextAlignment.Center)
                .MaxWidth(440),
            Button("Create a situation", add).AccentButton()
        ) with { RowGap = 16, AlignItems = FlexAlign.Center, JustifyContent = FlexJustify.Center, FlexPadding = new Thickness(32) };

    // "Active now" in the success color; "Off" and friends in neutral.
    public static Element StatusPill(string text, bool success) =>
        Border(Caption(text).Foreground(success ? Theme.PrimaryText : Theme.SecondaryText))
            .Background(success ? Theme.SystemSuccessBackground : Theme.SubtleFill)
            .CornerRadius(4)
            .Padding(6, 1);

    static string NewName(SituationState state)
    {
        for (int n = 1; ; n++)
        {
            string name = n == 1 ? "New situation" : $"New situation {n}";
            if (!state.Situations.Any(s => s.Name == name))
                return name;
        }
    }
}

record SituationListProps(SituationState State, string? SelectedId, Action<string?> Select);

// The left pane. A component of its own because Reactor re-renders from the root on any state
// change (each keystroke in the editor) and skips a Component<TProps> whose props are equal; and a
// templated ListView, keyed by id, so a real change (a rename landing, a situation turning active)
// updates rows in place instead of rebuilding and re-animating the list.
class SituationList : Component<SituationListProps>
{
    public override Element Render()
    {
        var state = Props.State;
        var select = Props.Select;
        var rows = state.Situations
            .Select(s => SituationRow(s, state.IsActive(s.Id), s.Id == Props.SelectedId, () => select(s.Id)).WithKey(s.Id))
            .ToArray();

        return FlexColumn(
            Caption("Situations switch monitors on or off while their conditions hold. When two want the same monitor, the higher one wins.")
                .Foreground(Theme.SecondaryText)
                .TextWrapping(TextWrapping.WrapWholeWords)
                .Margin(16, 0, 16, 0),
            Button(
                    FlexRow(Icon(FontIcon("", fontSize: 14)), TextBlock("New situation")) with { ColumnGap = 8, AlignItems = FlexAlign.Center },
                    () => SituationsEditor.AddSituation(state, select))
                .AccentButton()
                .AutomationName("New situation")
                .AutomationId("NewSituation")
                .Margin(16, 0, 16, 0),
            ScrollView((FlexColumn(rows) with { RowGap = 2 }).Padding(8, 0, 8, 0))
                .AutomationName("Situations")
                .Flex(grow: 1, basis: 0)
        ) with { RowGap = 12, FlexPadding = new Thickness(0, 4, 0, 8) };
    }

    // A list row: a subtle button with ListView's look (selected fill and accent pill), so it reads
    // and works like one while staying an ordinary keyed child that updates in place.
    static Element SituationRow(Situation situation, bool active, bool selected, Action onSelect)
    {
        string name = SituationText.Name(situation);
        string? status = !situation.Enabled ? "Off" : active ? "Active now" : null;

        var content = FlexRow(
            Border(Empty())
                .Width(3)
                .Height(16)
                .CornerRadius(1.5)
                .Background(Theme.Accent)
                .Opacity(selected ? 1 : 0),
            (FlexColumn(
                FlexRow(
                    BodyStrong(name).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis).Flex(grow: 1, shrink: 1, basis: 0),
                    status is null ? null : SituationsEditor.StatusPill(status, active && situation.Enabled)
                ) with { ColumnGap = 8, AlignItems = FlexAlign.Center },
                Caption(SituationText.Summary(situation))
                    .Foreground(Theme.SecondaryText)
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxLines(2)
            ) with { RowGap = 2 }).Flex(grow: 1, shrink: 1, basis: 0)
        ) with { ColumnGap = 10, AlignItems = FlexAlign.Center };

        // The selected fill is an overlay that fades by opacity: adding and removing a Background on
        // the button itself would knock it out of its subtle (transparent) look.
        var body = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Star()],
            Border(Empty()).Background(Theme.SubtleFill).CornerRadius(4).Opacity(selected ? 1 : 0).Grid(row: 0, column: 0),
            Border(content).Padding(4, 8, 12, 8).Grid(row: 0, column: 0));

        return Button(body, onSelect)
            .SubtleButton()
            .Set(b => b.HorizontalContentAlignment = HorizontalAlignment.Stretch)
            .HAlign(HorizontalAlignment.Stretch)
            .Padding(0)
            .AutomationName($"{name}{(status is null ? "" : $", {status}")}{(selected ? ", selected" : "")}");
    }
}
