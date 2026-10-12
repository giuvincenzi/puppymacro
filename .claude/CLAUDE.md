# PuppyMacro

Input automation for Windows 10/11 (loops, recorded macros, key remaps, overlay over fullscreen apps).
.NET 10 WPF app with iNKORE.UI.WPF.Modern (standard Windows 11 controls only), installed and updated
with Velopack from GitHub Releases.

The rules in `.claude/rules/` hold the details. Rules without `paths` load in every session;
the others load when matching files are read or edited:

| Rule | Loads | Topic |
|---|---|---|
| `working-rules.md` | always | How to work on this project |
| `git-workflow.md` | always | A worktree per change, pull requests, protected `main` |
| `build-and-release.md` | always | `make` commands, dev and prod builds, releases |
| `testing.md` | always | Unit tests (`make test`) and end-to-end tests (`make e2e`) |
| `docs.md` | always | Website, user guide in `site/` and AI tools, kept in sync with every change |
| `architecture.md` | `PuppyMacro/**` | Threads, engine snapshot, feature map |
| `input.md` | input pipeline files | Hooks, modifiers, injected input, routing order |
| `data.md` | models and storage | Data folders, settings migrations, compatibility |
| `ui.md` | XAML and views | Standard iNKORE.UI.WPF.Modern components only (strict) |
| `updates.md` | updater and packaging | Velopack, update flow |

## Known limits and decisions

- No overlay inside exclusive-fullscreen apps (would need DLL injection): overlay mode needs
  windowed fullscreen / borderless.
- If the target app runs as administrator, PuppyMacro must run as administrator too.
- Start with Windows uses the user Run key, so it cannot start elevated.
- Postponed idea: GitHub login, sync of user data (secret gist) and a public "store" of
  macros (issues in a public repository). Not started.
- AI apps reach PuppyMacro only through MCP from an app installed on the PC (Claude Desktop, VS Code, Codex…):
  claude.ai, ChatGPT and other apps on the web cannot reach a program on the PC without a relay on the internet,
  which the project would have to host and secure. Not planned. PuppyMacro has no AI of its own and no API keys.
- AI apps cannot start or test loops and macros: the input would go to the window in front, usually the AI chat.
