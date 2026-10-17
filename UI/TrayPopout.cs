using System;
using Microsoft.UI.Input;                // InputKeyboardSource
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // Component, Context, Element
using Microsoft.UI.Xaml;                 // ElementTheme, UIElement
using Windows.System;                    // VirtualKey
using Windows.UI.Core;                   // CoreVirtualKeyStates
using static Microsoft.UI.Reactor.Factories;

// Root Reactor component of the popout: picks the page (status or settings), follows the taskbar
// theme, and handles back/dismiss keys. App-lifetime state lives in Watchdog and PopoutStore (read
// through UseExternalStore), so the tree can be unmounted with its window and remounted later
// without losing anything.
class TrayPopout : Component
{
    // How the content asks its host to dismiss it (Escape). PopoutWindow provides its Hide; the
    // --devtools preview window has nothing to dismiss.
    public static readonly Context<Action> Dismiss = new(() => { }, nameof(Dismiss));

    public override Element Render()
    {
        var dismiss = UseContext(Dismiss);
        var page = UseExternalStore(PopoutStore.Subscribe, () => PopoutStore.Current.Page);
        bool taskbarIsLight = UseExternalStore(SubscribeToTaskbarTheme, () => SystemThemeService.IsLight);

        Element content = page == PopoutPage.Settings
            ? Component<SettingsPage>().WithKey("settings")
            : Component<StatusPage>().WithKey("status");

        // Transparent background so the shell-style acrylic shows through.
        return Border(content)
            .RequestedTheme(taskbarIsLight ? ElementTheme.Light : ElementTheme.Dark)
            .OnKeyDown((_, e) =>
            {
                // Escape goes back first and hides second; Alt+Left only goes back.
                bool altLeft = e.Key == VirtualKey.Left && IsDown(VirtualKey.Menu);
                if (e.Key != VirtualKey.Escape && !altLeft)
                    return;

                e.Handled = true;
                if (page == PopoutPage.Settings)
                    PopoutStore.Navigate(PopoutPage.Status);
                else if (!altLeft)
                    dismiss();
            })
            .OnPointerPressed((sender, e) =>
            {
                // Mouse back button.
                if (page == PopoutPage.Settings && e.GetCurrentPoint(sender as UIElement).Properties.IsXButton1Pressed)
                {
                    e.Handled = true;
                    PopoutStore.Navigate(PopoutPage.Status);
                }
            });
    }

    static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // UseExternalStore subscription for the taskbar theme. SystemThemeService raises Changed on a
    // registry-watch thread, so hop to the UI thread before Reactor re-reads the snapshot. A static
    // method keeps the delegate equal across renders, so the subscription isn't torn down each time.
    static Action SubscribeToTaskbarTheme(Action onChange)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Action handler = () => dispatcher.TryEnqueue(() => onChange());
        SystemThemeService.Changed += handler;
        return () => SystemThemeService.Changed -= handler;
    }
}
