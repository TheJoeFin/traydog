using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;          // DispatcherQueue
using Microsoft.UI.Reactor;
using Microsoft.UI.Xaml.Controls;        // MenuFlyout, MenuFlyoutItem, ToggleMenuFlyoutItem, FontIcon
using WinUIEx;                           // TrayIcon

// App lifetime for the tray-only app: owns the tray icon and its menu, the single-instance guard,
// and the window slots (the popout and the situations editor). There is no main window. Each window
// exists only while it's in use, and the tray menu's Exit is the only way out.
static class TrayApp
{
    // Startup leaves JIT and first-request state behind that the idle app never touches again.
    static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(30);

    static SingleInstance? _instance;
    static DispatcherQueue? _dispatcher;
    static TrayIcon? _trayIcon;
    static string? _trayIconFile;
    static PopoutWindow? _popout;
    static SituationsWindow? _situations;
    static ResourcesWindow? _resources;
    static bool _isExiting;

    // Runs on the UI thread from ReactorApp.Run's startup callback.
    public static void Start(SingleInstance instance)
    {
        _instance = instance;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        instance.ListenForShowRequests(() => _dispatcher.TryEnqueue(() => ShowPopout(fromTrayClick: false)));

        // Monitors resume the user's choice, then situations get their say before the icon appears.
        Watchdog.Start();
        ResourceMonitor.Start();
        Monitors.Register(InternetMonitor.Instance);
        Monitors.Register(ResourceMonitor.Instance);
        SituationEngine.Start();

        var state = Watchdog.State;
        _trayIconFile = StatusText.IconFile(state.Monitoring, state.Net.Status);
        _trayIcon = new TrayIcon(1, IconPath(_trayIconFile), Tooltip());
        _trayIcon.Selected += (_, _) => TogglePopout();
        _trayIcon.ContextMenu += OnContextMenu;
        _trayIcon.IsVisible = true;
        WindowPlacement.SetTrayIcon(_trayIcon);
        Watchdog.Changed += UpdateTrayIcon;
        SituationEngine.Changed += UpdateTrayIcon;
        ResourceMonitor.Changed += UpdateTrayIcon;
        ResourceMonitor.AlertRaised += OnResourceAlert;
        PopoutStore.Changed += UpdateProbeCadence;

        // First run: show where the app lives instead of launching into silence. Later launches
        // (including start with Windows) stay in the tray.
        if (!SettingsService.HasLaunchedBefore)
        {
            SettingsService.HasLaunchedBefore = true;
            ShowPopout(fromTrayClick: false);
        }
        else
        {
            MemoryService.ReleaseIdle(StartupTrimDelay);
        }
    }

    // Opens the situations editor, or brings it forward. It takes over from the popout.
    public static void ShowSituations()
    {
        _popout?.Hide();
        if (_situations is null)
        {
            var situations = new SituationsWindow();
            situations.Closed += () =>
            {
                if (_situations == situations)
                    _situations = null;
                ReleaseIdleIfNoWindows();
            };
            _situations = situations;
        }

        _situations.Activate();
    }

    public static void ShowResources()
    {
        _popout?.Hide();
        if (_resources is null)
        {
            var window = new ResourcesWindow();
            window.Closed += () =>
            {
                if (_resources == window)
                    _resources = null;
                ReleaseIdleIfNoWindows();
            };
            _resources = window;
        }
        _resources.Activate();
    }

    static void OnResourceAlert()
    {
        if (_popout?.IsVisible == true)
            return;

        PopoutStore.Navigate(PopoutPage.Status);
        EnsurePopout().Show(fromTrayClick: false, activate: false);
    }

    // Probe faster only while someone is looking at the status page (not behind Settings).
    static void UpdateProbeCadence()
    {
        var popout = PopoutStore.Current;
        Watchdog.SetStatusOnScreen(popout.IsVisible && popout.Page == PopoutPage.Status);
    }

    // Reflect monitoring status on the tray icon and tooltip. Watchdog.Changed also fires for
    // schedule updates, so only touch the shell when the icon file actually changes.
    static void UpdateTrayIcon()
    {
        if (_trayIcon is null)
            return;

        var state = Watchdog.State;
        string iconFile = ResourceMonitor.State.Alerts.Count > 0 ? "Warning.ico" : StatusText.IconFile(state.Monitoring, state.Net.Status);
        if (iconFile != _trayIconFile)
        {
            _trayIconFile = iconFile;
            _trayIcon.SetIcon(IconPath(iconFile));
        }

        string tooltip = Tooltip();
        if (tooltip != _trayIcon.Tooltip)
            _trayIcon.Tooltip = tooltip;
    }

    static string Tooltip()
    {
        var state = Watchdog.State;
        var situations = SituationEngine.State;
        var active = situations.Situations.Where(s => situations.IsActive(s.Id)).Select(SituationText.Name);
        var resources = ResourceMonitor.State;
        string tooltip = StatusText.Tooltip(state.Monitoring, state.Net.Status,
            new[] { resources.Alerts.Count > 0 ? $"{resources.Alerts.Count} process alert(s)" :
                resources.Running ? "Process monitoring on" : "" }.Where(s => s.Length > 0).Concat(active));
        return tooltip;
    }

    // WinUIEx loads the .ico by path at runtime; the .csproj copies every Assets\*.ico next to the exe.
    static string IconPath(string iconFile) => Path.Combine(AppContext.BaseDirectory, "Assets", iconFile);

    // Built fresh on every right-click so the toggles reflect live state. One toggle per monitor.
    static void OnContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        var open = new MenuFlyoutItem { Text = $"Open {StatusText.AppName}", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => ShowPopout(fromTrayClick: true);

        var situations = new MenuFlyoutItem { Text = "Situations", Icon = new FontIcon { Glyph = "" } };
        situations.Click += (_, _) => ShowSituations();

        var settings = new MenuFlyoutItem { Text = "Settings", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => ShowPopout(fromTrayClick: true, PopoutPage.Settings);

        var resources = new MenuFlyoutItem { Text = "Processes", Icon = new FontIcon { Glyph = ResourceMonitor.Instance.Glyph } };
        resources.Click += (_, _) => ShowResources();

        var exit = new MenuFlyoutItem { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => Exit();

        var menu = new MenuFlyout();
        menu.Items.Add(open);
        foreach (var monitor in Monitors.All)
        {
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = monitor.Name,
                IsChecked = Monitors.IsOn(monitor.Id),
                Icon = new FontIcon { Glyph = monitor.Glyph }
            };
            toggle.Click += (_, _) => Monitors.Toggle(monitor.Id);
            menu.Items.Add(toggle);
        }
        menu.Items.Add(situations);
        menu.Items.Add(resources);
        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        args.Flyout = menu;
    }

    // A tray click always lands on the status page; a light-dismiss leaves the page as it was, so
    // only reset it when this click is going to show the popout.
    static void TogglePopout()
    {
        var popout = EnsurePopout();
        if (!popout.IsVisible)
            PopoutStore.Navigate(PopoutPage.Status);
        popout.Toggle();
    }

    static void ShowPopout(bool fromTrayClick, PopoutPage page = PopoutPage.Status)
    {
        PopoutStore.Navigate(page);
        var popout = EnsurePopout();
        popout.Show(fromTrayClick);
    }

    // The popout closes itself after staying hidden for a while; the next open builds a new one.
    static PopoutWindow EnsurePopout()
    {
        if (_popout is null)
        {
            var popout = new PopoutWindow();
            popout.Closed += () =>
            {
                if (_popout == popout)
                    _popout = null;
                ReleaseIdleIfNoWindows();
            };
            _popout = popout;
        }

        return _popout;
    }

    static void ReleaseIdleIfNoWindows()
    {
        if (!_isExiting && _popout is null && _situations is null && _resources is null)
            MemoryService.ReleaseIdle();
    }

    static void Exit()
    {
        _isExiting = true;
        _popout?.Close();
        _situations?.Close();
        _resources?.Close();
        PopoutStore.Changed -= UpdateProbeCadence;
        Watchdog.Changed -= UpdateTrayIcon;
        SituationEngine.Changed -= UpdateTrayIcon;
        ResourceMonitor.Changed -= UpdateTrayIcon;
        ResourceMonitor.AlertRaised -= OnResourceAlert;
        SituationEngine.Stop();
        Monitors.Stop();

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _instance?.Dispose();
        ReactorApp.Exit();
    }
}
