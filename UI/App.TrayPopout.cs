using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Windowing;            // AppWindow, OverlappedPresenter, DisplayArea
using Microsoft.UI.Xaml;                 // Window, WindowActivatedEventArgs, WindowActivationState
using Microsoft.UI.Xaml.Controls;        // MenuFlyout, MenuFlyoutItem, ToggleMenuFlyoutItem
using Windows.Graphics;                  // PointInt32
using WinUIEx;                           // TrayIcon

// Tray-icon integration for the App component: turns the primary Reactor window into a
// borderless popout anchored to the notification area, registers the tray icon plus its
// context menu, and keeps the icon/tooltip in sync with connectivity status.
partial class App
{
    // Human-readable status for the tray tooltip.
    static string StatusText(bool monitoring, NetStatus s) =>
        !monitoring ? "monitoring off" :
        s switch
        {
            NetStatus.Online => "connected",
            NetStatus.NoInternet => "no internet (network up)",
            NetStatus.Offline => "offline",
            _ => "checking…"
        };

    // Status -> tray icon filename (under Assets/, copied next to the exe by the .csproj).
    static string IconFile(bool monitoring, NetStatus s) =>
        !monitoring ? "trayicon.ico" :
        s switch
        {
            NetStatus.Online => "Check mark button.ico",
            NetStatus.NoInternet => "Warning.ico",
            NetStatus.Offline => "Globe with meridians.ico",
            _ => "trayicon.ico" // still probing
        };

    void UseTrayPopout(bool monitoring, Action<bool> setMonitoring, NetState net)
    {
        // ReactorWindow this component is mounted in. null only outside a window component.
        var window = UseWindow();
        // Refs let the once-registered tray handlers read live values without re-registering.
        var trayRef = UseRef<TrayIcon?>(null);
        var monitoringRef = UseRef(false);
        var setMonitoringRef = UseRef<Action<bool>?>(null);
        monitoringRef.Current = monitoring;
        setMonitoringRef.Current = setMonitoring;

        // --- Mount once: window chrome, tray icon, and all event wiring. ---
        UseEffect(() =>
        {
            var native = window?.NativeWindow as Window;
            var appWindow = native?.AppWindow;
            var appIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "trayicon.ico");

            // Strip the window down to a borderless, fixed-size, floating popout that does
            // not appear in the taskbar or Alt-Tab.
            if (appWindow?.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }
            if (appWindow is not null)
            {
                appWindow.IsShownInSwitchers = false;
                appWindow.SetIcon(appIconPath);
            }
            if (native is not null)
                Interop.ApplyRoundedCorners(WinRT.Interop.WindowNative.GetWindowHandle(native));

            // Reactor activates the primary window during Run. Hide it until the tray icon has
            // registered, then show the initial popout beside that icon.
            window?.Hide();

            long lastHiddenTick = 0;

            void HidePopout()
            {
                lastHiddenTick = Environment.TickCount64;
                window?.Hide();
            }

            async Task ShowAt(PointInt32 anchor)
            {
                if (appWindow is not null)
                {
                    var area = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Nearest);
                    appWindow.Move(PopoutPlacement.Calculate(anchor, appWindow.Size, area.WorkArea));

                    window?.Show();
                    await Task.Delay(100);
                    window?.Activate();
                }
            }

            Task ShowPopout()
            {
                if (!Interop.GetCursorPos(out var cursor))
                    return Task.CompletedTask;

                return ShowAt(new PointInt32(cursor.X, cursor.Y));
            }

            void ShowPopoutInBackground() => _ = ShowPopout();

            async Task ShowInitialPopout()
            {
                // Explorer creates the notification-area rectangle asynchronously. Give it a
                // short window to register this icon before using the taskbar as a fallback.
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (Interop.TryGetNotificationAreaBounds(out var notificationArea))
                    {
                        var area = DisplayArea.GetFromPoint(
                            new PointInt32(
                                notificationArea.X + notificationArea.Width / 2,
                                notificationArea.Y + notificationArea.Height / 2),
                            DisplayAreaFallback.Nearest);
                        appWindow?.Move(PopoutPlacement.CalculateForTaskbar(
                            notificationArea, appWindow.Size, area.WorkArea));
                        window?.Show();
                        await Task.Delay(100);
                        window?.Activate();
                        return;
                    }

                    await Task.Delay(50);
                }

                if (Interop.TryGetTaskbarBounds(out var taskbar))
                {
                    var area = DisplayArea.GetFromPoint(
                        new PointInt32(
                            taskbar.X + taskbar.Width / 2,
                            taskbar.Y + taskbar.Height / 2),
                        DisplayAreaFallback.Nearest);
                    appWindow?.Move(PopoutPlacement.CalculateForTaskbar(
                        taskbar, appWindow.Size, area.WorkArea));
                    window?.Show();
                    await Task.Delay(100);
                    window?.Activate();
                    return;
                }

                await ShowPopout();
            }

            void TogglePopout()
            {
                // If this same click just dismissed the popout (focus loss fires first),
                // leave it closed so the icon behaves as a toggle.
                if (appWindow?.IsVisible == true || Environment.TickCount64 - lastHiddenTick < 250)
                    HidePopout();
                else
                    ShowPopoutInBackground();
            }

            // Auto-hide the popout the moment focus leaves it.
            void OnActivated(object sender, WindowActivatedEventArgs e)
            {
                if (e.WindowActivationState == WindowActivationState.Deactivated)
                    HidePopout();

                if (sender is not Window wind)
                    return;

                wind.SetForegroundWindow();
            }
            if (native is not null)
                native.Activated += OnActivated;

            // An external close (e.g. Alt+F4 while focused) should hide, not quit — the tray
            // icon is the app's real lifetime.
            void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
            {
                e.Cancel = true;
                HidePopout();
            }
            if (appWindow is not null)
                appWindow.Closing += OnClosing;

            // WinUIEx loads the .ico by path at runtime, so resolve it next to the exe.
            var tray = new TrayIcon(trayiconId: 1, iconPath: appIconPath, tooltip: "traydog");
            tray.IsVisible = true;
            tray.Selected += (_, _) => TogglePopout();
            tray.LeftDoubleClick += (_, _) => TogglePopout();

            // Right-click context menu, rebuilt each time so the toggle reflects live state.
            tray.ContextMenu += (_, e) =>
            {
                var monitor = new ToggleMenuFlyoutItem
                {
                    Text = "Internet monitoring",
                    IsChecked = monitoringRef.Current
                };
                monitor.Click += (_, _) => setMonitoringRef.Current?.Invoke(!monitoringRef.Current);

                var open = new MenuFlyoutItem { Text = "Open traydog" };
                open.Click += (_, _) => ShowPopoutInBackground();

                var exit = new MenuFlyoutItem { Text = "Exit" };
                exit.Click += (_, _) => ReactorApp.Exit();

                var flyout = new MenuFlyout();
                flyout.Items.Add(monitor);
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(open);
                flyout.Items.Add(exit);
                e.Flyout = flyout;
            };

            trayRef.Current = tray;
            _ = ShowInitialPopout();

            return () =>
            {
                if (native is not null)
                    native.Activated -= OnActivated;
                if (appWindow is not null)
                    appWindow.Closing -= OnClosing;
                tray.Dispose();
                trayRef.Current = null;
            };
        }, Array.Empty<object>());

        // --- Reflect status on the tray icon whenever monitoring or status changes. ---
        UseEffect(() =>
        {
            var tray = trayRef.Current;
            if (tray is not null)
            {
                tray.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", IconFile(monitoring, net.Status)));
                tray.Tooltip = $"traydog — {StatusText(monitoring, net.Status)}";
            }
            return () => { };
        }, monitoring, net.Status);
    }
}
