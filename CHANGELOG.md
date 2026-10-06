# Changelog

## Unreleased

## 0.15.0 - 2026-10-07

- Added opt-in global TacticalLink presence and direct peer telemetry with spatial interest filtering and reconnect handling.
- Added Cloud-issued short-lived identity tokens, per-peer protocol validation and rate limits, and direct callsign ownership safeguards.
- Added configurable 10/20 Hz TacticalLink telemetry, capability status, diagnostics and deployment configuration.
- Corrected 50- and 100-peer load tests to attempt 20 telemetry frames per second per peer.
- Fixed the app footer so connection, traffic and version details fit across the full window width.
- Fixed VTSD Cloud device-login completion to work with the Neon HTTP database driver.
- AAR fuel transfer and dedicated high-rate ownship sampling remain future work.

- Added the first global, opt-in TacticalLink protocol/server foundation with transient in-memory presence, spatial interest filtering, capability updates and direct peer telemetry.
- Added VTSD Cloud active VATSIM callsign resolution and short-lived RS256 TacticalLink JWT issuance.
- Added a TAC menu and persistent connection indicator, direct peer traffic tracks, and conservative SimConnect duplicate suppression.
- Prepared a tanker-capability profile field and availability protocol state; fuel transfer and AAR operations remain future work.
- Added TacticalLink reconnect, 10/20 Hz telemetry, interest-radius and diagnostics settings; server expires stale simulator telemetry and clamps client interest to a configured maximum.
- Reserved direct TacticalLink callsigns during VATSIM enrichment, aligned short-skew deduplication with motion, and separated network publishing cadence from simulator polling.
- Added a versioned TacticalLink wire protocol, per-connection sequence reset, per-peer rate limits, malformed-message handling, random public participant IDs and structured operational counters.
- Added expiring shared VATSIM identity caching and a Caddy TLS reverse proxy deployment alongside the internal TacticalLink service.
- TODO for future AAR work: evaluate a dedicated high-rate ownship sampling path for fresh 20 Hz AAR telemetry while keeping normal simulator traffic polling near 10 Hz.

## 0.14.13 - 2026-10-05

- Kept clearly supported callsign assignments when another formation component is ambiguous; weak matches now compete with an unmatched result.
- Replaced exponential callsign assignment enumeration with a polynomial cost-minimization search while preserving unmatched alternatives and stable pairs across plausible assignments.
- Kept fresh traffic visible through brief timestamp alignment gaps using degraded or short-lived retained geometry, without feeding unaligned positions into closure history.
- Added 6/8/10/12/15-contact ambiguous-formation performance coverage and exposed closure benchmark tables in GitHub Actions summaries.
- Added conservative closure regression limits for steady closure, step response, outlier recovery, alignment loss, vector disagreement and source transitions; preserved recency weighting and position-preferred source selection.
- Expanded throttled formation diagnostics with ranked candidate alternatives, generation and revocation details, and disconnect/reconnect ownership events.

## 0.14.12 - 2026-09-28

- Fixed MSFS traffic closure to use smoothed position-based range changes, with velocity-based startup fallback and diagnostics for comparing both calculations.
- Improved closure stability around target teleports and added explicit closure labels and high-speed head-on regression coverage.
- Improved tactical closure responsiveness by weighting recent position-derived range samples more heavily.

## 0.14.11 - 2026-08-23

- Reduced GPU work in the WebView2/Mapbox map layer by lowering camera updates to two per second and disabling unnecessary fade and world-copy rendering.

## 0.14.10 — 2026-08-05

- Fixed long-running memory growth in the WebView2/Mapbox map layer by coalescing map updates and limiting them to four per second.

## 0.14.7 — 2026-07-27

- Improved VATSIM callsign matching for MSFS traffic with conservative kinematic fallback matching.
- Added stronger ambiguity checks and multi-observation confirmation to reduce incorrect callsign assignments.
- Added a short-range fallback for contacts where SimConnect does not provide complete motion data.
- Added regression tests for offset, ambiguous, and incomplete-motion VATSIM matches.

## 0.14.6 — 2026-07-26

- Switched debug report uploads to direct Vercel Blob client multipart uploads.

## 0.14.5 — 2026-07-23

- Improved VATSIM callsign matching with timestamp-aware historical samples and position interpolation.
- Added monitor-local borderless fullscreen mode for multi-monitor setups.
- Added configurable global hotkeys for Windows joystick/HOTAS buttons alongside XInput controllers.
- Added `Ctrl+Shift+F` as the default fullscreen hotkey.
