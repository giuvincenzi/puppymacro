---
paths:
  - "PuppyMacro/Services/UpdateService.cs"
  - "PuppyMacro/MainWindow.Updates.cs"
  - "PuppyMacro/App.xaml.cs"
  - "PuppyMacro/PuppyMacro.csproj"
  - "scripts/build.ps1"
  - ".github/workflows/**"
---

# Updates and packaging

- The installed app updates from GitHub Releases (`Services/UpdateService.cs`,
  `MainWindow.Updates.cs`): check at startup and every 12 hours, bar at the top + dot on
  Settings while an update is available, Update stops all loops and macros, downloads,
  exits normally and Velopack restarts the new version.
- Velopack: `App.Main` runs `VelopackApp.Build().Run()` before WPF starts (App.xaml is a
  Page, `StartupObject` is `PuppyMacro.App`). Keep the `Velopack` package version and
  `$VpkVersion` in `scripts/build.ps1` equal.
- The package depends on the .NET 10 Desktop Runtime and the Microsoft Edge WebView2 Runtime
  (vpk frameworks `net10.0-x64-desktop,webview2`, for the editors' Code view): the Setup
  installs them when missing, and an update that adds one installs it first.
