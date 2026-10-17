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
- `UI/` — tray lifetime and the popout:
  - `TrayApp.cs` — owns the WinUIEx `TrayIcon`, its context menu (built fresh each right-click), the popout's window slot, and `Exit()`, the only way out. Updates the icon/tooltip from `Watchdog.Changed`.
  - `PopoutWindow.cs` — wraps a `ReactorApp.OpenWindow` window: tray-anchored placement, light-dismiss (150 ms debounce, popup-class exemptions), slide/fade via `ReactorWindow.SetOpacity`, Alt+F4 → hide, and **closes for real after 1 minute hidden** (then `MemoryService.ReleaseIdle`). The next open builds a new window.
  - `TrayPopout.cs` — root component: switches between `StatusPage` and `SettingsPage`, follows the taskbar theme (`RequestedTheme`), handles Escape / Alt+Left / mouse back. The host passes `Hide` down through the `TrayPopout.Dismiss` context.
  - `StatusPage.cs` (+ `RefreshBar`), `SettingsPage.cs`, `PopoutContent.cs` (shared page base: `UsePopout`, `UseFocusWhenShown`, `IconButton`, version).
  - `PopoutStore.cs` — app-lifetime `PopoutState` (page, visible, times shown) so the tray menu can open Settings directly and a tray click returns to Status.
  - `ShellBackdrop.cs` — acrylic that matches the shell's flyouts, given to Reactor as `WindowSpec.Backdrop = BackdropChoice.Of(() => backdrop)`.
- `Monitoring/` — connectivity domain: `Watchdog.cs` (app-lifetime `WatchdogState` + the `ConnectivityMonitor` it owns), `ConnectivityMonitor.cs` (ping + HTTP probe loop with a changeable interval), `NetState.cs`, and the pure `StatusText.cs` / `CheckInterval.cs`.
- `Platform/` — Win32 plumbing with no UI: `WindowPlacement.cs` (`Shell_NotifyIconGetRect` anchor, taskbar edge, DPI), the pure `PopoutPlacement.cs` geometry, `SingleInstance.cs`, `CrashLog.cs`.
- `Services/` — `SettingsService` (LocalSettings, defaults on any failure), `StartupService` (MSIX startup task `TrayDogStartup`), `SystemThemeService` (taskbar light/dark via registry watch), `MemoryService` (idle GC + working-set trim).
- `NativeMethods.txt` — Win32 APIs generated by CsWin32 (`Windows.Win32.PInvoke`); add to it instead of hand-writing `DllImport`.

Rules that keep it a good tray citizen:

- **State that outlives the popout lives outside components** (`Watchdog`, `PopoutStore`, `SettingsService`). Components read it with `UseExternalStore(Store.Subscribe, () => Store.Current)`; pass a *static* subscribe method so the subscription isn't torn down every render, and marshal background-thread events to the UI thread first.
- **Background work:** the only work while hidden is monitoring, because the tray icon *is* the status display (the skill's documented exception). It's opt-in and persisted, runs one probe at a time, and uses `Watchdog.InTrayInterval` (a setting) unless the status page is on screen (15 s). Don't add other hidden work.
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
`Platform\PopoutPlacement.cs`) instead of referencing the WinUI project. Put new decision logic
(parsing, policies, formatting, interval math) in files like these and link them; don't unit test
the UI — verify it with `winapp ui` against the running app. The app's `.csproj` excludes
`traydog.Tests\**` because the test project sits inside the app's folder.

## Build configuration notes (`traydog.csproj`)

The `.csproj` carries several deliberate, documented decisions — read its inline comments before changing them:

- **`WindowsAppSDKSelfContained=true`**: bundles the Windows App SDK runtime into each package, so no separate runtime install is needed. It also *sidesteps* a `Microsoft.WindowsAppSDK.ComponentReference.targets` strict-version check that otherwise breaks `dotnet watch run` hot reload.
- **`RuntimeIdentifier` auto-resolution**: conditional properties infer the RID from the host SDK, or from a pinned `Platform` (needed by the Store bundle's per-arch sub-builds). WindowsAppSDK's self-contained path requires a concrete RID.
- **Devtools are Debug-only**: `Microsoft.UI.Reactor.Devtools` and the `Reactor.DevtoolsSupport` host-config option live in a `Condition="'$(Configuration)' == 'Debug'"` ItemGroup so Release/Publish builds keep the trim/AOT analyzers happy.
- **Idle-footprint runtime knobs** (from the sibling tray apps): `ConcurrentGarbageCollection=false`, `TieredPGO=false`, `EventSourceSupport=false` outside Debug, and `System.Globalization.UseNls=true` (no ICU load).
- **CsWin32** (`NativeMethods.txt`) needs `AllowUnsafeBlocks`; it's compile-time only.
- `AppxBundleAutoResourcePackageQualifiers=Scale|DXFeatureLevel` in `Store` keeps languages in the main package instead of resource packs that never reach the bundle.
