# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`traydog` is a tray-only Windows app (notification-area icon + popout, no main window) built on **Microsoft.UI.Reactor** (`0.1.0-preview.12`), a React-style declarative UI framework layered over WinUI 3 / Windows App SDK. UI is expressed as `Component` classes with a `Render()` method returning an `Element` tree, not XAML. Targets `net10.0-windows10.0.22621.0`.

https://github.com/microsoft/microsoft-ui-reactor

It follows the `winui-tray-app` skill's conventions (tray-only lifetime, single instance, hide-then-close popout, shell-style backdrop, idle memory release), adapted to Reactor. Load that skill before changing app lifetime, the popout, the tray icon, polling, or settings. The Reactor agent docs (signatures index, windowing, hooks) ship in the package: `%USERPROFILE%\.nuget\packages\microsoft.ui.reactor\<version>\agentkit\`.

## Commands

```powershell
winapp run                   # build, register with package identity, launch (--detach to keep working)
winapp run --clean           # same, from a first-run state (first run opens the popout)
winapp run -c Release        # check a Release build before calling a feature done
dotnet build                 # host-architecture build (also produces a local MSIX; see below)
dotnet test traydog.slnx     # unit tests (traydog.Tests)
dotnet run --launch-profile "traydog Devtools"   # popout content in a plain window for Reactor devtools / VS preview
```

The app is **packaged (MSIX) in every configuration**, so launch it through `winapp run` or VS's
"traydog (Package)" profile; the bare `.exe` has no package identity (settings and the startup
task then fall back to defaults / hidden). A second launch doesn't start a second copy: it signals
the running instance to open its popout, which is also how to reach the popout from scripts
(`explorer.exe shell:AppsFolder\TrayDog_vek3qaanxbakt!App`), since the tray icon can't be clicked
by automation while the popout holds the foreground. Drive the running UI with
`winapp ui inspect/invoke/screenshot -w <hwnd>`.

`--devtools` (and the deprecated `--preview`) mount `TrayPopout` with `ReactorApp.Run<T>` in an
ordinary window: no tray icon, no single-instance guard. That's what the Reactor devtools and the VS
embedded preview need; the real app starts headless.

### Store (MSIX) build

**From Visual Studio:** pick **`Store`** in the configuration dropdown (x64 or ARM64), then
right-click the project → **Publish → Create App Packages…** and choose "Microsoft Store".
Reload the solution after editing the `.csproj` if the config/menu looks stale.

**From the command line** (produces the x64 + arm64 `.msixupload` bundle for Partner Center,
self-contained). Use **MSBuild**, not `dotnet` — `dotnet` doesn't drive the appx bundling
targets:

```powershell
# Path is VS-install specific; find via vswhere -find MSBuild\**\Bin\MSBuild.exe
& "<VS>\MSBuild\Current\Bin\MSBuild.exe" traydog.csproj /restore `
  /p:Configuration=Store /p:Platform=x64 `
  /p:AppxBundle=Always /p:AppxBundlePlatforms="x64|arm64" `
  /p:UapAppxPackageBuildMode=StoreUpload /p:AppxPackageSigningEnabled=false `
  /p:GenerateAppxPackageOnBuild=true /p:AppxPackageDir="AppPackages\"
```

Output: `AppPackages\traydog_<ver>_x64_arm64_bundle_Store.msixupload` — upload this to Partner
Center (it is intentionally unsigned; the Store re-signs it). `Package.appxmanifest`'s
`Identity/Name`, `Publisher`, and `PublisherDisplayName` **must exactly match** the app's
Partner Center reservation or the upload is rejected (`Publisher` and `PublisherDisplayName` come
from `D:\package-details.txt`). Store logo art (`Assets\*Logo*.png` etc., incl. `.scale-*` /
`.targetsize-*` variants) is generated from `Assets\Dog-face.png`.

### GitHub release

`build-msix-for-gh.ps1` (a copy of the shared `D:\build-msix-for-gh.ps1`; fix it there and recopy)
publishes signed x64 + ARM64 MSIX packages and drafts a GitHub release tagged from
`Identity/@Version`. It needs `Properties\PublishProfiles\win-x64.pubxml` / `win-arm64.pubxml`
(gitignored; restore them from a sibling app such as `D:\source\tray-radar`) and the maintainer's
`sign-msix` PowerShell function. Bump `Identity/@Version` before every release.

There is no linter config or CI in this repo yet.

## Architecture

Source is organized by concern:

- `Program.cs` — **entry point** (top-level statements): crash logging, the `--devtools` preview path, the single-instance handoff, then `ReactorApp.Run(startup)` with `ShutdownPolicy.Explicit` — a headless start with **no window at launch**.
- `UI/` — tray lifetime, the popout, and the situations window:
  - `TrayApp.cs` — owns the WinUIEx `TrayIcon`, its context menu (built fresh each right-click; one toggle per registered monitor), the popout's and situations window's slots, and `Exit()`, the only way out. Starts `Watchdog`, registers the monitors, then starts `SituationEngine`. Updates the icon/tooltip from `Watchdog.Changed` and `SituationEngine.Changed`.
  - `SituationsWindow.cs` — the situations editor: an ordinary Mica window (situations outgrow the popout), single instance, closes for real on X. While open, `SituationEngine.SetEditorOpen(true)` runs the cheap sensors for live status. `SituationsEditor.cs` (list + empty state) and `SituationEditor.cs` (one situation, plus `ConditionCard` with each condition kind's controls) are its components.
  - `ResourcesWindow.cs` / `ResourcesPage.cs` — the process monitor's ordinary Mica settings, thresholds, and executable-name rules. `ResourceAlertsContent.cs` is the shared, keyed multi-alert list in both the monitoring popout and process settings. Selection is by PID + creation time; Select all is a snapshot. New alerts append, reminders update their existing slot, and overflow never evicts an existing row. Alerts open the main popout without activation or programmatic focus; there is no separate alert window.
  - `PopoutWindow.cs` — wraps a `ReactorApp.OpenWindow` window: tray-anchored placement, light-dismiss (150 ms debounce, popup-class exemptions), slide/fade via `ReactorWindow.SetOpacity`, Alt+F4 → hide, and **closes for real after 1 minute hidden** (then `MemoryService.ReleaseIdle`). `MonitoringLayout` chooses height from the pending count on opening; placement clamps to the work area. Never resize/reanchor for arrivals while visible: users may be about to click a row. Retain `WindowStartPosition.Default`: Reactor reapplies `ManualPosition` on first show, overriding pre-show physical placement. The next open builds a new window.
  - `TrayPopout.cs` — root component: switches between `StatusPage` and `SettingsPage`, follows the taskbar theme (`RequestedTheme`), handles Escape / Alt+Left / mouse back. The host passes `Hide` down through the `TrayPopout.Dismiss` context.
  - `StatusPage.cs` (+ `RefreshBar`), `SettingsPage.cs`, `PopoutContent.cs` (shared page base: `UsePopout`, `UseFocusWhenShown`, `IconButton`, version).
  - `PopoutStore.cs` — app-lifetime `PopoutState` (page, visible, times shown) so the tray menu can open Settings directly and a tray click returns to Status.
  - `ShellBackdrop.cs` — acrylic that matches the shell's flyouts, given to Reactor as `WindowSpec.Backdrop = BackdropChoice.Of(() => backdrop)`.
- `Monitoring/` — the monitors and the connectivity domain:
  - `Monitors.cs` — `IMonitor` (id, name, glyph, `SetRunning`) and the `Monitors` switchboard: each registered monitor's `MonitorSwitch` (the user's usual choice, persisted via `SettingsService.Get/SetMonitorOn`; the current situation demand; a user override). The popout and tray call `Monitors.Choose/Toggle`, never a monitor directly.
  - `InternetMonitor.cs` — internet monitoring as an `IMonitor` (id `internet`, which keeps the legacy `MonitoringEnabled` setting key).
  - `Watchdog.cs` (app-lifetime `WatchdogState` + the `ConnectivityMonitor` it owns), `ConnectivityMonitor.cs` (ping + HTTP probe loop with a changeable interval), `NetState.cs`, and the pure `StatusText.cs` / `CheckInterval.cs`.
- `Situations/` — situations: named sets of conditions that switch monitors while they hold.
  - Pure (linked into the tests): `Situation.cs` (the model: `Situation`, the `Condition` kinds, `SituationContext`, `SensorKind`), `SituationPolicy.cs` (`MonitorSwitch`, demands with list-order priority, next clock check, distance), `SituationText.cs`, `SituationJson.cs` (source-generated, polymorphic on `kind`).
  - `SituationEngine.cs` — app-lifetime store (`SituationState`): loads/saves via `SituationStore` (`situations.json` in LocalFolder, debounced), runs only the sensors the enabled situations need, folds sensor bursts into one evaluation on the UI thread, and hands demands to `Monitors.ApplyDemands`.
  - `Sensors/` — `NetworkSensor` (profile names + SSID on `NetworkStatusChanged`), `PowerSensor` (Windows App SDK `PowerManager`), `DeviceSensor` (`DeviceWatcher` over external device containers, matched by name), `LocationSensor` (one quick fix every 10 minutes and on network change, not a live subscription).
- `Platform/` — Win32 plumbing with no UI: `WindowPlacement.cs` (`Shell_NotifyIconGetRect` anchor, taskbar edge, DPI), the pure `PopoutPlacement.cs` geometry, `SingleInstance.cs`, `CrashLog.cs`.
- `Resources/` — opt-in process monitoring (`IMonitor` id `resources`, registered in both startup paths). `ResourceModel.cs` and `ResourcePolicy.cs` are WinRT-free: defaults, rates, PID + creation-time identities, per-resource sustained confirmation, reminders, and name/instance mutes. `ProcessSampler` disposes process handles after each sample. `ResourceMonitor` owns one adaptive timer: full scans every 30 seconds by default, only suspect PIDs every 1 second for 20 seconds. `ResourceStore` atomically saves `resources.json`. The optional `ResourceCollectorClient` starts an elevated copy of the same exe (`--resource-collector`, handled before WinUI or single-instance startup); `ResourceCollector` totals actual disk and TCP/UDP events with a small real-time ETW buffer, no trace files/stacks/payloads. The pipe is current-user-only and verifies both endpoint PIDs. Only a user gesture can prompt for elevation; disabling monitoring/closing the pipe stops the collector. Event loss is surfaced and invalidates I/O confirmation. Process-start/exit events drive on-sight rules and release exited-instance state promptly.
- `Services/` — `SettingsService` (LocalSettings, defaults on any failure), `StartupService` (MSIX startup task `TrayDogStartup`), `SystemThemeService` (taskbar light/dark via registry watch), `MemoryService` (idle GC + working-set trim).
- `NativeMethods.txt` — Win32 APIs generated by CsWin32 (`Windows.Win32.PInvoke`); add to it instead of hand-writing `DllImport`.

Rules that keep it a good tray citizen:

- **State that outlives the popout lives outside components** (`Watchdog`, `Monitors`, `SituationEngine`, `PopoutStore`, `SettingsService`). Components read it with `UseExternalStore(Store.Subscribe, () => Store.Current)`; pass a *static* subscribe method so the subscription isn't torn down every render, and marshal background-thread events to the UI thread first.
- **Background work:** only opt-in monitoring and the situation sensors that decide when it runs. Connection monitoring runs one probe at a time and uses `Watchdog.InTrayInterval` unless Status is on screen (15 s). Process monitoring has its own adaptive scan/confirmation timer and optional elevated ETW collector; it never uses generic process I/O counters as a substitute for separate Disk/Network metrics. Situation sensors are event-driven except location (a fix every 10 minutes). Don't add other hidden work.
- **Adding a monitor:** implement `IMonitor`, register it in `TrayApp.Start` (and the `--devtools` path in `Program.cs`) after `Watchdog.Start()`. Situations, the editor's "Then" section, and the tray menu pick it up from `Monitors.All`. Ids are persisted in `situations.json`, so never rename one.
- **Adding a condition kind:** derive from `Condition` in `Situation.cs` and add its `[JsonDerivedType]` (the discriminator is persisted, so never rename it), describe it in `SituationText.Describe`, give it a sensor in `SituationEngine` if it reads a new `SensorKind`, add controls in `ConditionCard` and an entry in `SituationEditor.AddConditionButton`, and cover it in `SituationTests`. Unknown sensor data must make `Test` return null, so neither "is" nor "is not" fires.
- Anything the popout subscribes to or starts must stop in `PopoutWindow.OnClosed` or a component effect cleanup, or the closed window stays reachable and idle memory never drops. Idle target: ~10–25 MB working set (`Get-Process traydog`).
- Tray icons are full-color emoji `.ico` files, so they need no light-taskbar variants. Any new monochrome icon would (see the skill).
- Settings are a page inside the popout. If they outgrow one popout height, move to a settings window rather than adding scrolling sections.

Reactor idioms:

- **Components** subclass `Microsoft.UI.Reactor.Core.Component` and override `Element Render()`. State uses React-style hooks: `var (value, setValue) = UseState(initial)`. Calling a setter re-renders. Hooks run in the same order every render; custom hooks are methods on the component (or a shared base like `PopoutContent`), since `Component`'s hook methods are protected.
- **Element construction** is via factory functions imported with `using static Microsoft.UI.Reactor.Factories;` — e.g. `FlexColumn(...)`, `Border(...)`, `Heading(...)`, `Card(...)`, `ToggleSwitch(...)`, `ComboBox(...)`. These return `Element` records configured with C# `with` expressions (e.g. `... with { RowGap = 16 }`) and fluent extension methods (`.Padding(24)`, `.Flex(grow: 1, basis: 0)`, `.AutomationName(...)`). Type-specific sugar goes before generic modifiers.
- **Layout** is flexbox-based (`Microsoft.UI.Reactor.Layout`): `FlexDirection`, `FlexJustify`, `FlexAlign`, plus `.Flex(grow/shrink/basis)` and gap properties.
- Don't put `.Backdrop(...)` on the popout's elements: the window's `ShellBackdrop` comes from its `WindowSpec`.

## Tests

`traydog.Tests` is a plain `net10.0` MSTest project that **links** the app's WinRT-free files
(`Monitoring\StatusText.cs`, `CheckInterval.cs`, `ConnectivityMonitor.cs`, `NetState.cs`,
`Platform\PopoutPlacement.cs`, and `Situations\Situation.cs`, `SituationPolicy.cs`,
`SituationText.cs`, `SituationJson.cs`) instead of referencing the WinUI project. Put new decision logic
(parsing, policies, formatting, interval math) in files like these and link them; don't unit test
the UI — verify it with `winapp ui` against the running app. The app's `.csproj` excludes
`traydog.Tests\**` because the test project sits inside the app's folder.
Resource tests also link `Resources\ResourceModel.cs`, `ResourcePolicy.cs`, and the WinRT-free
`ProcessSampler.cs`; they cover rates, confirmation, cooldowns, rules, PID reuse, defaults/JSON,
and sampling an owned process instance.

## Build configuration notes (`traydog.csproj`)

The `.csproj` carries several deliberate, documented decisions — read its inline comments before changing them:

- **`WindowsAppSDKSelfContained=true`**: bundles the Windows App SDK runtime into each package, so no separate runtime install is needed. It also *sidesteps* a `Microsoft.WindowsAppSDK.ComponentReference.targets` strict-version check that otherwise breaks `dotnet watch run` hot reload.
- **`RuntimeIdentifier` auto-resolution**: conditional properties infer the RID from the host SDK, or from a pinned `Platform` (needed by the Store bundle's per-arch sub-builds). WindowsAppSDK's self-contained path requires a concrete RID.
- **Devtools are Debug-only**: `Microsoft.UI.Reactor.Devtools` and the `Reactor.DevtoolsSupport` host-config option live in a `Condition="'$(Configuration)' == 'Debug'"` ItemGroup so Release/Publish builds keep the trim/AOT analyzers happy.
- **Idle-footprint runtime knobs** (from the sibling tray apps): `ConcurrentGarbageCollection=false`, `TieredPGO=false`, `EventSourceSupport=false` outside Debug, and `System.Globalization.UseNls=true` (no ICU load).
- **CsWin32** (`NativeMethods.txt`) needs `AllowUnsafeBlocks`; it's compile-time only.
- `AppxBundleAutoResourcePackageQualifiers=Scale|DXFeatureLevel` in `Store` keeps languages in the main package instead of resource packs that never reach the bundle.
