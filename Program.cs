using System;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;                 // Application

AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);

if (args.Length > 0 && args[0] == "--resource-collector")
{
    if (args.Length == 3 && int.TryParse(args[2], out int parentPid))
        await ResourceCollector.RunAsync(args[1], parentPid);
    else
        CrashLog.Write(new ArgumentException("Invalid resource collector arguments."));
    return;
}

// --devtools (and the deprecated --preview): mount the popout content in an ordinary window so
// Reactor devtools and the VS embedded preview can drive it. No tray icon, no single instance.
if (Array.Exists(args, a => a is "--devtools" or "--preview"))
{
    ReactorApp.Run<TrayPopout>(StatusText.AppName, PopoutWindow.Width, PopoutWindow.Height,
        configure: _ =>
        {
            Watchdog.Start();
            ResourceMonitor.Start();
            Monitors.Register(InternetMonitor.Instance);
            Monitors.Register(ResourceMonitor.Instance);
            SituationEngine.Start();
            PopoutStore.SetVisible(true);
        });
    return;
}

// A second launch hands off to the running instance (which opens its popout) and exits.
var instance = SingleInstance.TryAcquire();
if (instance is null)
{
    SingleInstance.SignalRunningInstance();
    return;
}

// Tray-only: no window at launch. The popout window opens and closes on demand, and only the tray
// menu's Exit ends the process.
ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
ReactorApp.Run(_ =>
{
    Application.Current.UnhandledException += (_, e) => CrashLog.Write(e.Exception);
    TrayApp.Start(instance);
});
