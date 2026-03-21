# Data Model

Core entities:
- `OwnshipState`
- `TrafficContactState`
- `TrafficSnapshot`
- `ComputedTarget`
- `TacticalPicture`

Repository responsibilities:
- keep latest target state by ID
- update category from local callsign metadata
- mark stale targets
- prune expired targets
- keep limited trail history
