# PuppyMacro

Input automation for Windows 10/11 (loops, recorded macros, key remaps, in-game panel).
.NET 10 WPF app with WPF-UI, installed and updated with Velopack from GitHub Releases.

The rules in `.claude/rules/` hold the details. Rules without `paths` load in every session;
the others load when matching files are read or edited:

| Rule | Loads | Topic |
|---|---|---|
| `working-rules.md` | always | How to work on this project |
| `build-and-release.md` | always | `make` commands, dev and prod builds, releases |
| `testing.md` | always | Unit tests (`make test`) and end-to-end tests (`make e2e`) |
| `docs.md` | always | Website and user guide in `site/`, kept in sync with every change |
| `architecture.md` | `PuppyMacro/**` | Threads, engine snapshot, feature map |
| `input.md` | input pipeline files | Hooks, modifiers, injected input, routing order |
| `data.md` | models and storage | Data folders, settings migrations, compatibility |
| `ui.md` | XAML and views | Fluent look, WPF-UI controls |
| `updates.md` | updater and packaging | Velopack, update flow |

## Known limits and decisions

- No overlay inside exclusive-fullscreen games (would need DLL injection): game mode needs
  windowed fullscreen / borderless.
- If the target app runs as administrator, PuppyMacro must run as administrator too.
- Start with Windows uses the user Run key, so it cannot start elevated.
- Postponed idea: GitHub login, sync of user data (secret gist) and a public "store" of
  macros (issues in a public repository). Not started.
