using System;
using Microsoft.UI.Dispatching;          // DispatcherQueue
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // Component, Element
using Microsoft.UI.Reactor.Input;        // ElementRef
using Microsoft.UI.Xaml;                 // FocusState
using Microsoft.UI.Xaml.Controls;        // Control
using Windows.ApplicationModel;          // Package
using static Microsoft.UI.Reactor.Factories;

// Base for the popout's pages: the hooks and pieces they share.
abstract class PopoutContent : Component
{
    protected static readonly string Version = ReadVersion();

    protected PopoutState UsePopout() => UseExternalStore(PopoutStore.Subscribe, () => PopoutStore.Current);

    // Returns a ref to bind to the control that should take focus each time the page appears (the
    // popout opens, or navigation lands here). That gives keyboard users a starting point and
    // Escape a target. Deferred past the reconcile pass so the control exists.
    protected ElementRef UseFocusWhenShown(PopoutState popout)
    {
        var target = UseMemo(() => new ElementRef());

        UseEffect(() =>
        {
            if (popout.IsVisible && popout.FocusOnShow)
                DispatcherQueue.GetForCurrentThread().TryEnqueue(() => FocusWhenLoaded(target.Current as Control));
        }, popout.TimesShown);

        return target;
    }

    // A page that just mounted (navigation) isn't loaded yet, and Focus() on it silently fails.
    static void FocusWhenLoaded(Control? control)
    {
        if (control is null)
            return;

        if (control.IsLoaded)
        {
            control.Focus(FocusState.Programmatic);
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            control.Loaded -= OnLoaded;
            control.Focus(FocusState.Programmatic);
        }
        control.Loaded += OnLoaded;
    }

    // 36x32 transparent header button with a Segoe Fluent glyph, named and tooltipped for
    // accessibility since it has no text.
    protected static Element IconButton(string glyph, string name, Action? onClick = null) =>
        Button(Icon(FontIcon(glyph, fontSize: 14)), onClick)
            .SubtleButton()
            .Width(36)
            .Height(32)
            .Padding(0)
            .AutomationName(name)
            .ToolTip(name);

    static string ReadVersion()
    {
        try
        {
            var v = Package.Current.Id.Version;
            return $"Version {v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "Development build"; // no package identity (--devtools profile)
        }
    }
}
