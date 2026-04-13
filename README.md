# Virtual Tactical Situation Display

Virtual Tactical Situation Display is a Windows application for a clear 2D tactical air picture around your own aircraft.

**Simulator use only. Not for real-world aviation, air traffic control, or operational decision-making.**

![VTSD logo](VTSD.png)

## Features

- own aircraft position and heading
- nearby simulator traffic
- friend, package, support, enemy, and unknown target symbols
- range, bearing, altitude difference, target heading, aspect, and closure labels
- target trails
- active V-LARA reserved airspace boundaries
- user-defined bullseye reference point

## Screenshot

![Tactical Situation Display screenshot](TacticalDisplay.App.png)

## Quick Start

1. Download the latest release.
2. Start `TacticalDisplay.App.exe`.
3. Use `Demo` first to check the display.
4. Select `MSFS`, `XPlane 12`, or `Xplane Legacy (XPUIPC)` from `Source` when using live simulator data.
5. Click `Apply Source`.

## Data Sources

- `Demo` uses built-in test traffic.
- `MSFS` uses Microsoft Flight Simulator through SimConnect.
- `XPlane 12` uses the X-Plane 12 local Web API.
- `Xplane Legacy (XPUIPC)` uses X-Plane through XPUIPC.

SimConnect is bundled with the app. X-Plane 12 uses `http://localhost:8086/` by default; check that the X-Plane Web API is available and incoming traffic is not disabled. For legacy X-Plane support, install XPUIPC into `X-Plane\Resources\plugins`.

## Display Controls

- `Range +` / `Range -`: change visible range
- `N/HDG`: switch north-up / heading-up
- `Declutter`: reduce display clutter
- `Labels`: cycle label detail level
- `Trails ON/OFF`: show or hide target trails
- `Areas`: show or hide active V-LARA airspace boundaries
- `Area opacity`: adjust airspace layer opacity
- `Save Settings`: keep current settings for the next launch

Keyboard shortcuts:
- `Ctrl+H`: show or hide the settings panel
- `Ctrl+D`: toggle declutter
- `Ctrl+T`: pin or unpin the window on top

## Targets

Symbols:
- friend = circle
- package = diamond
- support = square
- enemy = cross
- unknown = dot

Mouse actions:
- left click a target: cycle affiliation
- right click a target: rename
- drag a label: move that label
- middle click a target or label: hide or show that label

## Airspace

The app can show active V-LARA reserved airspace boundaries. It loads EFIN airspace geometry and uses the V-LARA reservations feed to highlight active reservations.

Use `Areas` to toggle the layer.

## Bullseye

Enter latitude and longitude in the settings panel, then click `Set Bullseye`.

Accepted examples:
- `N60.3172` and `E024.9633`
- `60.3172N` and `024.9633E`
- `60.3172` and `24.9633`

`S` and `W` work normally. Click `Clear Bullseye` to remove it.

When the bullseye is within the selected range, the display shows its symbol plus bearing/range from ownship.

## Troubleshooting

If live data does not appear, check:
- the correct source is selected
- the simulator is running
- XPUIPC is installed when using legacy X-Plane
- X-Plane 12 local Web API URL is correct when using `XPlane 12`

If the map layer says `Map unavailable`, check that Microsoft Edge WebView2 Runtime is installed and that the app can write to `%APPDATA%\VirtualTacticalSituationDisplay\WebView2`. A `0x8000FFFF` startup error usually means WebView2 failed before the map page or online tiles loaded.

Data source logs are written to `%APPDATA%\VirtualTacticalSituationDisplay\logs\data-source-debug.log` when debug logging is enabled in the app.

## Advanced Notes

Settings, target metadata, cache files, WebView2 data, and debug logs are stored under `%APPDATA%\VirtualTacticalSituationDisplay`.

Most users can use the UI, but advanced users can inspect files such as `display.json`, `friends.json`, `package.json`, `support.json`, and `manual-targets.json` when needed. If a settings file cannot be loaded after an update or manual edit, the app replaces it with clean defaults so startup can continue.

Airspace geometry and activation feed URLs are also stored in `display.json`.
