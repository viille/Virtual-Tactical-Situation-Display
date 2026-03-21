# Tactical Situation Display Architecture

## Phase 1 (Desktop MVP)
- `TacticalDisplay.Core`: domain models, repository, stale/removal lifecycle, tactical computations.
- `TacticalDisplay.Rendering`: ownship-centered projection helpers.
- `TacticalDisplay.App`: WPF UI, interaction, and active data feed adapters (`SimConnectTrafficFeed`, `DemoTrafficFeed`).
- `TacticalDisplay.Config`: local JSON settings and callsign metadata.

## Runtime Flow
1. Feed receives ownship + traffic snapshot.
2. `TrafficRepository` updates tracks, stale state, and history.
3. `TacticalComputationEngine` computes range, bearing, rel-alt, closure.
4. WPF renderer draws rings, symbols, headings, labels, trails.

## Phase 2 Plan
- Keep `Core` unchanged and reuse it in cockpit-specific front-end.
- Add WASM gauge adapter with `init/update/draw` hooks.
- Reuse same target model and filtering logic.
