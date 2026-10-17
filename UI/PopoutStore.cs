using System;

enum PopoutPage
{
    Status,
    Settings
}

// What the popout content needs to know about its host: which page to show, whether the window is
// on screen, and how many times it has been shown (each show is a fresh "open" for focus and for
// re-reading state that can change outside the app).
record PopoutState(PopoutPage Page, bool IsVisible, int TimesShown);

// App-lifetime popout state, outside the component tree so the tray menu can open the popout
// straight onto Settings and a tray click can return it to Status. Components read it through
// UseExternalStore; PopoutWindow reports visibility. UI thread only.
static class PopoutStore
{
    public static PopoutState Current { get; private set; } = new(PopoutPage.Status, false, 0);

    // Raised on the UI thread whenever Current changes.
    public static event Action? Changed;

    // UseExternalStore's subscribe shape: returns the unsubscribe action.
    public static Action Subscribe(Action onChange)
    {
        Changed += onChange;
        return () => Changed -= onChange;
    }

    public static void Navigate(PopoutPage page) => Publish(Current with { Page = page });

    public static void SetVisible(bool visible)
    {
        if (visible == Current.IsVisible)
            return;

        Publish(Current with { IsVisible = visible, TimesShown = Current.TimesShown + (visible ? 1 : 0) });
    }

    static void Publish(PopoutState next)
    {
        if (next == Current)
            return;

        Current = next;
        Changed?.Invoke();
    }
}
