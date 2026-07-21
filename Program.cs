using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

// Tray-first app: the process stays alive on the tray icon alone, so nothing must exit
// when the (always-hidden-until-clicked) popout window goes away.
ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
ReactorApp.Run<App>("traydog", width: 320, height: 360);
