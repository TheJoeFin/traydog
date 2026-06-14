using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;          // DispatcherQueue
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // BackdropKind
using Microsoft.UI.Windowing;            // AppWindow, OverlappedPresenter, DisplayArea
using Microsoft.UI.Xaml;                 // Window, WindowActivatedEventArgs, WindowActivationState
using Microsoft.UI.Xaml.Controls;        // MenuFlyout, MenuFlyoutItem, ToggleMenuFlyoutItem
using Windows.Graphics;                  // PointInt32
using WinUIEx;                           // TrayIcon
using static Microsoft.UI.Reactor.Factories;

// Tray-first app: the process stays alive on the tray icon alone, so nothing must exit
// when the (always-hidden-until-clicked) popout window goes away.
ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
ReactorApp.Run<App>("traydog", width: 320, height: 360);

class App : Component
{
    public override Element Render()
    {
        var (monitoring, setMonitoring) = UseState(false);
        var (net, setNet) = UseState(new NetState(NetStatus.Unknown, "Not monitoring"));

        UseTrayPopout(monitoring, setMonitoring, net);
        UseConnectivity(monitoring, setNet);

        // Frameless popout — no TitleBar; the window chrome is stripped in UseTrayPopout.
        var body = Border(
            FlexColumn(
                Heading("traydog"),
                TextBlock($"Internet: {StatusText(monitoring, net.Status)}"),
                TextBlock(monitoring ? net.Detail : "Monitoring is off").Opacity(0.7),
                Button(monitoring ? "Stop monitoring" : "Start monitoring",
                    () => setMonitoring(!monitoring)).AutomationName("ToggleMonitoring")
            ) with { RowGap = 12 }
        ).Padding(20).Flex(grow: 1, basis: 0);

        return FlexColumn(body)
            .Backdrop(BackdropKind.Mica);
    }

    // Human-readable status for the popout and tooltip.
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

    // Custom hook: turns the primary window into a tray-anchored popout and lets the tray
    // icon both drive the window and reflect internet status.
    //   - A WinUIEx system-tray icon (NotifyIcon) owns showing/hiding the window.
    //   - The window is frameless, always-on-top, and absent from taskbar/Alt-Tab.
    //   - Clicking the icon toggles the popout, positioned above the cursor.
    //   - The right-click menu carries an "Internet monitoring" toggle + Exit.
    //   - A second effect swaps the icon/tooltip whenever monitoring or status changes.
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

            // Strip the window down to a borderless, fixed-size, floating popout that does
            // not appear in the taskbar or Alt-Tab.
            if (appWindow?.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }
            if (appWindow is not null)
                appWindow.IsShownInSwitchers = false;

            // Tray-first: Reactor activates the primary window during Run, so hide it
            // immediately — it should only appear when the tray icon is clicked.
            window?.Hide();

            long lastHiddenTick = 0;

            void HidePopout()
            {
                lastHiddenTick = Environment.TickCount64;
                window?.Hide();
            }

            void ShowPopout()
            {
                // Anchor over the cursor (the tray click lands on the icon), then clamp to
                // the work area of whichever monitor the cursor is on so it stays on-screen.
                if (appWindow is not null && Interop.GetCursorPos(out var cursor))
                {
                    var area = DisplayArea.GetFromPoint(
                        new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
                    var work = area.WorkArea;
                    var size = appWindow.Size; // physical pixels

                    int x = cursor.X - size.Width / 2;
                    int y = cursor.Y - size.Height - 12; // above the cursor (taskbar usually at bottom)
                    x = Math.Clamp(x, work.X, work.X + work.Width - size.Width);
                    y = Math.Clamp(y, work.Y, work.Y + work.Height - size.Height);
                    appWindow.Move(new PointInt32(x, y));
                }

                window?.Show();
                window?.Activate();
            }

            void TogglePopout()
            {
                // If this same click just dismissed the popout (focus loss fires first),
                // leave it closed so the icon behaves as a toggle.
                if (appWindow?.IsVisible == true || Environment.TickCount64 - lastHiddenTick < 250)
                    HidePopout();
                else
                    ShowPopout();
            }

            // Auto-hide the popout the moment focus leaves it.
            void OnActivated(object sender, WindowActivatedEventArgs e)
            {
                if (e.WindowActivationState == WindowActivationState.Deactivated)
                    HidePopout();
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
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "trayicon.ico");
            var tray = new TrayIcon(trayiconId: 1, iconPath: iconPath, tooltip: "traydog");
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
                open.Click += (_, _) => ShowPopout();

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

    // Custom hook: runs a ConnectivityMonitor while monitoring is on, marshalling its
    // background status updates back onto the UI thread.
    void UseConnectivity(bool monitoring, Action<NetState> setNet)
    {
        UseEffect(() =>
        {
            if (!monitoring)
            {
                setNet(new NetState(NetStatus.Unknown, "Not monitoring"));
                return () => { };
            }

            var dispatcher = DispatcherQueue.GetForCurrentThread();
            setNet(new NetState(NetStatus.Unknown, "Checking…"));

            var monitor = new ConnectivityMonitor();
            monitor.StatusChanged += (status, detail) =>
                dispatcher?.TryEnqueue(() => setNet(new NetState(status, detail)));
            monitor.Start();

            return () => monitor.Dispose();
        }, monitoring);
    }
}

static class Interop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);
}
