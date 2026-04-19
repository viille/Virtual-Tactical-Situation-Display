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
- adjustable target symbol size
- virtual MFD-style frame with on-screen controls
- active V-LARA reserved airspace boundaries
- user-defined bullseye reference point

## Screenshot

![Tactical Situation Display screenshot](TacticalDisplay.App.png)

## Quick Start

1. Download the latest release.
2. Start `TacticalDisplay.App.exe`.
3. Use `Demo` first to check the display.
4. Click `SET` to open or hide the settings panel.
5. Select `MSFS`, `XPlane 12`, or `Xplane Legacy (XPUIPC)` from `Source` when using live simulator data.
6. Click `Apply Source`.

## Data Sources

- `Demo` uses built-in test traffic.
- `MSFS` uses Microsoft Flight Simulator through SimConnect.
- `XPlane 12` uses the X-Plane 12 local Web API.
- `Xplane Legacy (XPUIPC)` uses X-Plane through XPUIPC.

SimConnect is bundled with the app. X-Plane 12 uses `http://localhost:8086/` by default; check that the X-Plane Web API is available and incoming traffic is not disabled. For legacy X-Plane support, install XPUIPC into `X-Plane\Resources\plugins`.

## Display Controls

The app opens directly into the tactical display. The settings panel is hidden by default and can be opened with `SET`.
The current orientation and range are shown in the upper-left corner of the display surface.

## Tablet Display

The desktop app starts a lightweight web display on port `8787`. Open the `Web: http://...:8787/` address shown in the app footer from a tablet or another device on the same local network.

The tablet display renders the Mapbox map and tactical canvas on the device and stays synchronized with the desktop app. It includes controls for range, orientation, map opacity, map, declutter, trails, bullseye, intercept target selection, labels, LARA airspace, controlled airspace layers, pin, settings, and target symbol size. These controls update the same display state as the main window controls.

The tablet display can be turned on or off with the `WEB` frame button next to `SET`.

If the tablet cannot connect, allow the app through Windows Firewall for private networks and check that both devices are connected to the same network.

Frame controls:
- `RNG +` / `RNG -`: change visible range
- `N/HDG`: switch north-up / heading-up
- top up/down arrows: increase or decrease map opacity
- `MAP`: show or hide the map layer
- `DCLR`: reduce display clutter
- `TRAIL`: show or hide target trails
- `BE`: show or hide bullseye
- `LARA`: show or hide active V-LARA airspace boundaries
- `AREA`: switch between the base map and the TMA/CTR/CTA map style
- `INT`: select or clear one intercept target
- `PIN`: pin or unpin the window on top
- `LBL`: cycle label detail level
- `SET`: show or hide the settings panel
- `WEB`: turn tablet web server on or off
- `TGT +` / `TGT -`: increase or decrease target symbol size

The scope uses four range rings at 1/4, 1/2, 3/4, and full selected range. Heading is shown as an `HDG` readout at the top of the radar area, and the compass reference is drawn as a circular outer compass instead of radial lines through the display.

Window controls:
- the app uses its own borderless window controls
- drag empty, non-functional parts of the virtual frame to move the window
- resize from the window edge
- the last normal window size is saved when the app closes

Settings panel controls:
- `Area opacity`: adjust airspace layer opacity
- `Save Settings`: keep current settings for the next launch

Keyboard shortcuts:
- `Ctrl+H`: show or hide the settings panel
- `Ctrl+D`: toggle declutter
- `Ctrl+T`: pin or unpin the window on top

## Targets

Intercept target mode:
- click `INT`, then left click a target to mark it for intercept
- with `INT` armed, left click either the target symbol or its label
- the display draws a line to the selected target, highlights the target, and shows intercept heading and time to intercept in the top `INT` readout next to `HDG`
- `NO INT` means the current speed and target motion do not produce an intercept solution
- click `INT` again to clear the current intercept target

Symbols:
- friend = circle
- package = diamond
- support = square
- enemy = cross
- unknown = dot

Mouse actions:
- click `INT`, then left click a target: select or clear the intercept target
- left click a target: cycle affiliation
- right click a target: rename
- right click a label: rename that target
- drag a label: move that label
- middle click a target or label: hide or show that label

## Airspace

The app can show active V-LARA reserved airspace boundaries. It loads EFIN airspace geometry and uses the V-LARA reservations feed to highlight active reservations.

Use `LARA` to toggle the layer.

## Bullseye

Enter latitude and longitude in the settings panel, then click `Set Bullseye`.
After coordinates have been set, use `BE` to show or hide bullseye without clearing the saved coordinates.

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

If the map layer says `Map unavailable`, check that Microsoft Edge WebView2 Runtime is installed and online map access is available. A `0x8000FFFF` startup error usually means WebView2 failed before the map page or online tiles loaded.

Data source logs are written to `%APPDATA%\VirtualTacticalSituationDisplay\logs\debug.log` when debug logging is enabled in the app.
