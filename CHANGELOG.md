# Changelog

## 0.16.1 - 2026-10-10

- Fix duplicate ownship targets in MSFS by filtering traffic mirrors using horizontal distance.

## 0.16.0-alpha.4 - 2026-10-10

- Keep previously confirmed callsign labels visible through temporary matcher gaps for 30 seconds; revoke them if supporting evidence does not return.
- Keep range rings at exactly 25%, 50%, 75% and 100% of the selected range and show distinct whole-number NM labels.
- Skip throttled debug message construction entirely when diagnostic logging is disabled.
- Restore WebView2 map message handlers after control reload and recover the map after a WebView2 process failure.
- Detect an unresponsive Mapbox WebView2 renderer with an idle-safe heartbeat and reload it when necessary.
- Index historical traffic samples once per matcher pass and retry failed AAR bridge handshakes at a bounded interval.
- Align the outer selected-range ring and plotted tactical contacts with the compass rose perimeter.
- Keep debug logging lightweight by removing repeated full VATSIM candidate ranking and historical rematching from the live snapshot path.
- Recover the Mapbox WebView2 layer when WebGL context is lost, render frames stop, or map state acknowledgements stop.
- Keep the TacticalLink tanker join control visible and explain when connection or aircraft registry capability prevents joining.
- Match VATSIM callsigns for simulator traffic up to 100 NM when altitude, motion, and global assignment identify a unique pilot; position-only fallback remains short-range.
- Prefer an MSFS/SDK-specific SimConnect DLL before the bundled client and log whether the loaded DLL exposes the MSFS 2024 CommBus functions needed by the AAR bridge.
- Bundle the MSFS 2024 SDK SimConnect client automatically when the SDK is available at build time, so end users do not need the SDK installed.
- Keep a live SimConnect feed open when an AAR CommBus call produces MSFS's internal exception; fail the bridge request without recycling telemetry.
- Added tanker-controlled AAR queue management, planned onload changes, astern clearance, HOLD, explicit transfer start/stop, and DryHookup mode to TacticalLink.Server.
- Added receiver-side internal transfer proposal data and cumulative application acknowledgements with operation revision validation.
- Added local KG/LB presentation and specific-amount or FULL receiver requests.
- Added the MSFS 2024 VTSD AAR Bridge, versioned CommBus protocol, desktop adapter, and read-after-write fuel acknowledgement path. The WASM module builds with the installed MSFS 2024 SDK; in-simulator validation is still pending.
- Routed AAR protocol state through typed `AarClient`/`AarState` modules, kept operational presentation in `AarViewModel`, and moved proposal application into `AarFuelTransferCoordinator`.
- Added the X-Plane 12 built-in Local Web API fuel adapter and a separate 10 Hz ownship pose sampler that does not accelerate normal traffic polling; API unit/capacity assumptions and runtime read/write behavior still require simulator validation. XP12 API negotiation fails closed for unknown versions. The MSFS SDK is not used for XP12.
- Restored the receiver's pending request after reconnect, made queue source and request intent visible, and added tanker-authoritative TOP/UP/DOWN/BOTTOM ordering.
- Added the tanker active-operation summary for receiver, aircraft, request intent, plan, transferred mass, remaining mass, and fuel authorization.
- Added state- and role-aware AAR command enablement; DryHookup does not enable fuel transfer.
- Added bridge status and package management UI, safe Community2024 install/update/uninstall, and an embedded single-file package archive path.
- Distinguishes invalid installed bridge packages from missing packages, supports repair, and preserves the previous package backup if rollback fails.
- Contact loss returns the operation to Astern and revokes clearance; committed-next promotion and reconciliation require a fresh CLEAR ASTERN before another contact clearance.
- v0.16 AAR eligibility is role-capability based and does not enforce Boom/Drogue compatibility. Registry method metadata remains available for conservative flow selection; the 10 kg/s fallback is generic. Broad procedural contact envelopes remain unchanged, and only tanker `START_TRANSFER` authorizes fuel.
- Hardened cancelled in-flight fuel proposals so verified simulator read-backs can settle accounting without restoring fuel authorization; fuel watermarks are scoped to operation IDs.
- Added bounded terminal AAR retention, failure cleanup and queue promotion, DryHookup reconnect reconciliation, and pending-request cancellation when leaving tanker mode.
- Added MSFS AAR runtime diagnostics to `debug.log`, including bridge negotiation, fuel discovery/mutations, runtime transitions, and installer path/stage details. See `docs/msfs-aar-bridge-acceptance.md` before runtime qualification.
- Added draft F-15 and F-16 receiver registry profiles with ICAO designator and boom-method sources in Cloud migration `0005_aar_receiver_seed`.
- Made v0.16 AAR eligibility method-agnostic: enabled tanker/receiver role capabilities are sufficient, while registry method metadata remains available for conservative flow selection and future method-aware behavior. Contact remains a broad procedural region; only the tanker can authorize fuel with `START_TRANSFER`. See `docs/aar-v0.16-policy.md`.

## 0.15.0 - 2026-10-07

- Made TacticalLink aircraft identity and capabilities server-authoritative from the active VATSIM flight plan.
- Expanded the desktop map by equalizing the side control columns and removing excess display margins.
- Moved the desktop intercept readout to the bottom center so it no longer overlaps the heading readout.
- Fixed the top-right window controls so the help button remains visible with the expanded map layout.
- Added opt-in global TacticalLink presence and direct peer telemetry with spatial interest filtering and reconnect handling.
- Added Cloud-issued short-lived identity tokens, per-peer protocol validation and rate limits, and direct callsign ownership safeguards.
- Added configurable 10/20 Hz TacticalLink telemetry, capability status, diagnostics and deployment configuration.
- Corrected 50- and 100-peer load tests to attempt 20 telemetry frames per second per peer.
- Fixed the app footer so connection, traffic and version details fit across the full window width.
- Restored the separate MFD bottom control bar while keeping connection and traffic details in the full-width window footer.
- Fixed VTSD Cloud device-login completion to work with the Neon HTTP database driver.
- AAR simulator fuel transfer and dedicated high-rate ownship sampling are planned for v0.16.0; live simulator acceptance remains pending.

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
