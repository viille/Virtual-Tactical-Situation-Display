# Repository instructions

## Purpose
- Virtual Tactical Situation Display is a Windows WPF/.NET 8 app for simulator-only tactical air picture rendering.
- Treat this as a desktop-first app. The tablet UI is a synchronized web companion, not the primary source of truth.
- Avoid making aviation-real-world claims. README and UI should keep simulator-only framing.

## Repository shape
- Solution: `tactical-situation-display.sln`
- Desktop app: `src/TacticalDisplay.App`
- Core models/services/math/config: `src/TacticalDisplay.Core`
- Tests: `src/TacticalDisplay.Tests`
- Watchdog and updater are embedded into the app publish output through MSBuild targets in `TacticalDisplay.App.csproj`.
- Local build outputs belong under `artifacts/` and are ignored by git.

## Architecture notes
- `MainWindow` owns `MainViewModel` and WPF lifecycle.
- `MainViewModel` owns settings, data feed selection, traffic repository, computed `TacticalPicture`, airspace/airport/navaid data, and user commands.
- `TacticalScopeControl` renders the tactical overlay in WPF.
- `OpenFreeMapControl` is the WebView2 map layer. Despite the old class name, current work is moving the map base to Mapbox.
- `WebDisplayServer` is a lightweight local TCP HTTP server for tablet use on port 8787.
- The tablet page renders its own map/canvas and syncs via `/api/snapshot` and `/api/command`. Do not reintroduce desktop screen mirroring unless specifically requested.

## Tablet web server
- Enabled by `TacticalDisplaySettings.EnableWebServer`.
- Desktop MFD has a WEB toggle next to SET; settings panel also has Tablet Web controls.
- Footer shows `Web: http://...:8787/` when running and `Web: off` when disabled.
- Web page intentionally does not include a WEB button, because turning the server off from the page would cut off the current session.
- Web page has a FULL button for browser fullscreen.

## Mapbox handling
- Do not commit a real Mapbox token.
- App supports build-time injection through MSBuild properties:
  ```powershell
  dotnet publish src\TacticalDisplay.App\TacticalDisplay.App.csproj -c Release -r win-x64 --self-contained true -p:DefaultMapboxAccessToken="pk..." -p:DefaultMapboxStyleUrl="mapbox://styles/<user>/<style>"
  ```
- `TacticalDisplay.App.csproj` writes those properties into assembly metadata.
- `MapboxDefaults` reads assembly metadata at runtime.
- Runtime precedence should stay:
  1. `display.json` / settings UI values
  2. build-injected assembly metadata defaults
  3. fallback style `mapbox://styles/mapbox/outdoors-v12`
- A public Mapbox token embedded in a release binary is still recoverable. Treat it as public, restricted, revocable, and separate from other projects.

## Build and verification
- Standard build: `dotnet build tactical-situation-display.sln`
- Standard tests: `dotnet test tactical-situation-display.sln --no-build`
- Local publish without embedded Mapbox defaults:
  `dotnet publish src\TacticalDisplay.App\TacticalDisplay.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\local-build`
- Local publish with embedded Mapbox defaults:
  `dotnet publish src\TacticalDisplay.App\TacticalDisplay.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\local-build -p:DefaultMapboxAccessToken="pk..." -p:DefaultMapboxStyleUrl="mapbox://styles/<user>/<style>"`
- Publish is slow because `TacticalDisplay.App.csproj` publishes updater and watchdog first.
- If publish to an existing artifacts folder fails because `TacticalDisplay.App.exe` is in use, publish to a new artifacts folder instead of killing user processes unless asked.

## Git workflow notes
- `main` tracks `origin/main`.
- Before pushing, check `git status --short --branch`.
- If local `main` is behind `origin/main`, commit local work first if needed, then `git pull --rebase origin main`.
- Never reset or discard user changes without explicit instruction.
- Version is in `src/TacticalDisplay.App/TacticalDisplay.App.csproj` under `<Version>`.
- Release tags use `vX.Y.Z` style.

## Coding style
- Prefer existing ViewModel commands and settings model over parallel state.
- Keep WPF command behavior as the source of truth; web commands should call the same ViewModel commands through Dispatcher.
- Keep comments sparse and useful.
- Use ASCII in source unless editing existing UI glyphs that already use entities or Unicode.
- For manual edits, use `apply_patch`.
- Keep web-companion up-to-date.
- Keep `CHANGELOG.md` up to date with user-facing changes.

## Known sensitive files
- `.devnote` is local-only and ignored.
- Never put real tokens or private release credentials in README, source, or committed config.
