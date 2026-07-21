# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`traydog` is a Windows desktop app built on **Microsoft.UI.Reactor** (`0.1.0-preview.4`), a React-style declarative UI framework layered over WinUI 3 / Windows App SDK. UI is expressed as `Component` classes with a `Render()` method returning an `Element` tree, not XAML. Targets `net10.0-windows10.0.22621.0`.

https://github.com/microsoft/microsoft-ui-reactor

## Commands

```powershell
dotnet build                 # x64 by default (Platforms ordered x64;ARM64;X86)
dotnet run                   # F5 equivalent — launches the "traydog" profile
dotnet run --launch-profile "traydog Devtools"   # run with Reactor devtools (--devtools)
dotnet watch run             # hot reload (relied on by the VS embedded-preview extension)
dotnet publish -c Release    # self-contained publish (~30 MB larger, no runtime install needed)
```

There is no test project, linter config, or CI in this repo yet.

## Architecture

Source is organized by concern:

- `Program.cs` — **entry point**: top-level statements `ReactorApp.Run<App>("traydog", width, height)` boot the framework and mount the root `App` component. There is no `Main`, no XAML, and no `MainWindow` — Reactor owns the window/host.
- `UI/` — the `App` component, a `partial class` split across three files: `App.cs` (`Render()` + popout presentation helpers), `App.TrayPopout.cs` (the `UseTrayPopout` hook — borderless-window setup, tray icon, context menu, tooltip), and `App.Connectivity.cs` (the `UseConnectivity` and `UseRefreshProgress` hooks).
- `Monitoring/` — connectivity domain: `NetState.cs` (the `NetStatus` enum + `NetState` record shared with the UI) and `ConnectivityMonitor.cs` (the background ping/HTTP poller).
- `Platform/` — Windows-desktop plumbing with no UI: `Interop.cs` (Win32/DWM P/Invoke) and `PopoutPlacement.cs` (pure popout-positioning geometry).

- **Components** subclass `Microsoft.UI.Reactor.Core.Component` and override `Element Render()`. State uses React-style hooks: `var (value, setValue) = UseState(initial)`. Calling a setter re-renders.
- **Element construction** is via factory functions imported with `using static Microsoft.UI.Reactor.Factories;` — e.g. `FlexColumn(...)`, `Border(...)`, `Heading(...)`, `TextBox(value, setter, ...)`, `TitleBar(...)`. These return `Element` records configured with C# `with` expressions (e.g. `... with { RowGap = 16 }`) and fluent extension methods (`.Padding(24)`, `.Flex(grow: 1, basis: 0)`, `.Backdrop(BackdropKind.Mica)`, `.AutomationName(...)`).
- **Layout** is flexbox-based (`Microsoft.UI.Reactor.Layout`): `FlexDirection`, `FlexJustify`, `FlexAlign`, plus `.Flex(grow/shrink/basis)` and gap properties.

## Build configuration notes (`traydog.csproj`)

The `.csproj` carries several deliberate, documented decisions — read its inline comments before changing them:

- **`WindowsAppSDKSelfContained=true`**: bundles the Windows App SDK runtime into the publish output. Beyond letting the app run without a separate runtime install, this *sidesteps* a `Microsoft.WindowsAppSDK.ComponentReference.targets` strict-version check that otherwise breaks `dotnet watch run` hot reload. Flip to `false` only if you accept a separate runtime install and want a smaller publish.
- **`RuntimeIdentifier` auto-resolution**: a conditional property infers the RID from the host SDK when `Platform`/`RuntimeIdentifier` aren't pinned, so `dotnet build`/`dotnet run` work without `-p:Platform=x64`. WindowsAppSDK's self-contained path requires a concrete RID.
- **Devtools are Debug-only**: `Microsoft.UI.Reactor.Devtools` and the `Reactor.DevtoolsSupport` host-config option live in a `Condition="'$(Configuration)' == 'Debug'"` ItemGroup so Release/Publish builds keep the trim/AOT analyzers happy. To enable devtools in Release, move them into the unconditioned ItemGroup.
