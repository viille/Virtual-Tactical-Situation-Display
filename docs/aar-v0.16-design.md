# VTSD v0.16.0 AAR module design

## Status and scope

This document is the implementation design for the first operational Air-to-Air Refueling (AAR) module in VTSD. Version 0.16.0 work is planned for separate desktop and VTSD Cloud branches. This design phase adds documentation only; it does not implement the module, change the protocol, modify the database, or publish a release.

VTSD is a simulator-only application. AAR registry entries describe VTSD simulator capability, never real-world AAR compatibility, clearance, or operational suitability.

The VATSIM `flight_plan.aircraft_short` value is already carried as the signed `aircraft_type` identity claim in the v0.15 architecture. It is the authoritative aircraft designator used by the server; adding this claim is not v0.16 work. Client-supplied telemetry, capability updates, simulator model names, and addon identifiers cannot grant capabilities.

## Ownership and module boundary

TacticalLink remains the authenticated realtime transport and presence platform. AAR is a module dispatched through a generic module boundary, not a collection of AAR-specific branches in the hub. A future Combat or Formation module must be able to use the same transport without depending on AAR types or state.

The platform envelope keeps TacticalLink protocol version 1 and introduces additive module messages:

```json
{
  "type": "MODULE_MESSAGE",
  "module": "aar",
  "moduleProtocolVersion": 1,
  "messageId": "client-generated-id",
  "kind": "REQUEST_REFUEL",
  "operationId": null,
  "payload": {}
}
```

Server-originated `MODULE_EVENT` messages use the same module and version fields, plus `kind`, `operationId`, `sequence`, and a module-owned payload. `messageId` is the idempotency key for a client command; the server binds it to participant, connection generation, and command result. Unknown module/version/message kinds are rejected without terminating the TacticalLink connection. Clients report that AAR is unavailable when the server does not support its module protocol. The hub performs common authentication, framing, rate limiting, and participant routing; an AAR handler owns AAR validation and state transitions.

The implementation boundary is a generic module router/handler interface, an AAR domain/state-machine service, and an in-memory operation manager. The server authorizes capabilities from signed `aircraft_type` plus the current published registry snapshot. Local adapter readiness may restrict use further but can never grant registry capability.

## Supported operation and state

V0.16 supports human-controlled tankers and receivers, boom refueling only, one active receiver per tanker, and an in-memory FIFO queue of additional requests. Probe/drogue operations, dedicated boom-operator controls, hose simulation, multiple simultaneous receiver operations, AI participants, complete simulator fuel-system simulation, and persistence of active operations are out of scope.

Tanker availability is `Off`, `Available`, `Busy`, or `Unavailable`. The pilot explicitly joins as tanker, then selects Available; Busy is derived from an active operation. After an operation, availability returns to Available unless the pilot selected Unavailable or left tanker mode. Receiver has no persistent role mode. Discovery requires a connected peer, tanker registry capability, local adapter readiness, Available state, compatible boom methods, and normal TacticalLink interest. An active operation pins both participants into interest regardless of normal radius.

Receiver requests a fixed kilogram amount or `FULL` (receiver capacity minus current fuel). Tanker explicitly accepts or rejects. Accepted requests reserve fuel and enter FIFO ordering by request time; distance does not change queue order. The server controls this state machine:

```text
Requested -> Accepted -> PreContact -> ClearedContact -> Contact
          -> Refueling -> Disconnecting -> Complete
          -> Rejected | Cancelled | Breakaway | Failed
```

Legal transitions are explicitly constrained: Requested may become Accepted, Rejected, Cancelled, or Failed; Accepted may become PreContact, Cancelled, or Failed; PreContact may become ClearedContact, Cancelled, Breakaway, or Failed; ClearedContact may become Contact, Breakaway, or Failed; Contact may become Refueling, Disconnecting, Breakaway, or Failed; Refueling may become Disconnecting, Breakaway, or Failed; Disconnecting may become Complete or Failed. Rejected, Cancelled, Breakaway, Complete, and Failed are terminal for that operation. A retry after a terminal result creates a new request/operation ID.

Clients send intent/actions; only the server advances authoritative operation state. Repeated commands with the same `messageId` return the original outcome and do not create another request, commitment, or transfer.

## Fuel model and transfer safety

All canonical amounts are kilograms. UI preference may display kilograms or pounds using `1 kg = 2.2046226218 lb`; changing units never alters operation values. `FULL` is resolved at request acceptance from receiver capacity minus current fuel. A protected tanker reserve is operator-configurable and has no hard-coded military policy.

At acceptance, planned amount is bounded by request, receiver free capacity, and tanker fuel available to promise. The accounting invariant is:

```text
outstandingCommitmentKg = sum(max(0, plannedKg - transferredKg))
availableToPromiseKg = max(0, currentTankerFuelKg
                              - protectedReserveKg
                              - outstandingCommitmentKg)
```

Transferred fuel is removed from current fuel and also reduces the outstanding commitment, so it is never subtracted twice. The server locks the accepted plan and effective rate for the operation.

Per-side limits preserve provenance: `confirmed aircraft-specific value`, `method fallback`, or `unknown`. Unknown boom limits use the conservative VTSD method fallback independently on each side:

```text
DEFAULT_BOOM_LIMIT_KG_PER_SECOND = 10
tankerLimit = tanker.MaxOffloadKgPerSecond ?? DEFAULT_BOOM_LIMIT_KG_PER_SECOND
receiverLimit = receiver.MaxReceiveKgPerSecond ?? DEFAULT_BOOM_LIMIT_KG_PER_SECOND
effectiveFlowKgPerSecond = min(tankerLimit, receiverLimit)
```

The 10 kg/s fallback is not an aircraft-specific performance claim and must never be shown as a confirmed type value in either desktop UI or Cloud admin. A known high tanker limit therefore cannot bypass an unknown receiver limit.

Only the server advances monotonic `TransferredKg`, and only during valid Refueling state. Clients apply deltas against their last successfully applied cumulative amount: receiver adds fuel and tanker removes it. Each acknowledges cumulative applied amount. The server pauses if either acknowledgement exceeds the configured lag bound; it stops on simulator write failure and never rolls fuel back. Engine burn remains independent. Stop conditions include planned amount reached, receiver full, tanker reserve boundary, disconnect, contact loss, stale pose, adapter loss, excessive acknowledgement lag, breakaway, or uncertain write outcome. No action automatically resumes transfer after reconnect.

Relevant fuel status is sent privately to the operation participants at 1–2 Hz; it is not added to global high-rate telemetry. The receiver UI may show request and operational state, but never current fuel, transferred amount, remaining amount, rate, ETA, percentage, or progress bar. The tanker UI may show current/reserve/committed/available fuel, active receiver, request/planned/transferred/remaining amounts, flow/ETA, contact state, queue, and operator actions.

The tanker control surface includes `JOIN AS TANKER`, `LEAVE TANKER MODE`, `ACCEPT`, `REJECT`, `CLEAR CONTACT`, `DISCONNECT`, and immediate `BREAKAWAY`. Before an operation the receiver may see available tanker, distance, requested onload, `FULL`, and request status. During it, the receiver sees only operational messages (for example request accepted, proceed/hold pre-contact, cleared contact, contact, disconnect, or breakaway). KG/LB is a presentation preference only; there is no liters mode.

## MSFS integration, pose, and contact

V0.16 targets MSFS first. Fuel access is through an `IAarFuelAdapter` contract for current fuel, capacity, write availability, and applying a signed mass delta. The first adapter uses generic SimConnect fuel quantities. Unsupported or uncertain writes fail closed; no silent synthetic transfer is allowed.

AAR samples ownship pose on a dedicated asynchronous path independent of WPF dispatch cadence and normal traffic polling. It targets about 20 Hz only while an AAR operation is active, maintains a short timestamped history, and aligns participant samples by timestamp with bounded interpolation/extrapolation. Existing `TacticalTelemetry` has optional pitch/bank fields, but the normal publisher currently sends them as null and `OwnshipState` does not contain them. MSFS AAR pose acquisition must provide fresh latitude/longitude/altitude, heading, pitch, bank, timestamp, and velocity. If any required value is unavailable or stale, AAR is not ready or transfer stops. Do not infer zero pitch/bank.

Use explicit configuration bounds, with units, for maximum sample age, maximum alignment gap, bounded extrapolation, and telemetry-loss timeout. The initial values are simulator parameters to be selected and calibrated during implementation tests. They do not represent real-world contact limits. Global traffic polling remains at its normal cadence; only AAR ownship pose sampling rises to approximately 20 Hz while an operation is active. Use a background timer/latest-value path and bounded history rather than WPF timer scheduling or unbounded buffering.

The geometry model defines aircraft body axes as +X forward, +Y right/starboard, +Z up. It transforms aircraft-body contact/receptacle offsets through attitude into WGS84 Earth-centered coordinates and then into a common local East/North/Up relative frame; velocity is compared in that same frame. Registry geometry may provide aircraft-family values and optional local technical overrides. Missing aircraft-specific geometry uses explicit, configurable synthetic simulation geometry; it is identified as such and calibrated in simulator testing, not presented as sourced aircraft data. Exact addon/model matching is not required, and future local overrides can restrict readiness for a technical incompatibility without expanding server capability.

Contact capture requires the server's configured 3D position, relative attitude, and relative-velocity envelope to be satisfied continuously for a debounce interval. Release uses a larger hysteresis envelope and a stale-data timeout to prevent contact chatter. Geometry alone never starts fueling: tanker CLEAR CONTACT is a required explicit action after stable PreContact. Breakaway or envelope violation stops transfer before subsequent fuel deltas are issued. All envelope values and timing bounds are configuration with documented units and initial test values; they are calibrated as simulation behavior.

## Reconnect, identity, and failure behavior

Connection loss immediately stops transfer and marks the operation interrupted/failed according to its current state. Reconnect does not restore fueling. A new clearance requires the same authenticated participant/connection-generation relationship, fresh pose, ready adapter, reconciled simulator fuel, and new valid contact. An `AUTH_REFRESH` identity change re-evaluates registry capability; loss of required capability terminates the operation. Server restart drops transient operations and queue, while the persistent registry cache remains independent.

Also stop or prevent flow on missing/stale pose, fuel-status timeout, fuel adapter loss/write failure, application-acknowledgement lag, receiver full, reserve reached, contact loss, simulator aircraft change, identity change, or uncertain write outcome. The default is `uncertain state -> no fuel flow`. An operation peer relationship pins interest independently of normal spatial-radius membership for the operation lifetime, including teardown events.

The server validates message identity, operation ownership, legal state transition, registry version, participant capability, and local readiness before acting. Invalid commands return module-scoped errors and do not affect other TacticalLink modules. Rates, queue length, payload size, and per-participant command frequency are bounded. Operational logs include operation ID, state changes, registry version, stop reason, and adapter outcomes; avoid logging secrets or unnecessary personal data.

## Implementation acceptance tests

- Signed aircraft identity grants only registry-derived capability; client payloads and local readiness cannot grant capability.
- Module routing rejects unknown versions/kinds safely and isolates AAR from other modules; duplicate command IDs are idempotent.
- State transitions, tanker availability, FIFO order, cancellation/rejection, and one-active-receiver constraints are deterministic.
- Commitment accounting does not double-subtract transferred fuel; both missing flow limits independently use 10 kg/s; confirmed values retain provenance and fallback is never displayed as aircraft-specific.
- Requests are bounded by receiver capacity, tanker reserve, and available-to-promise; monotonic transfer stops at every defined guard.
- Adapter failure, uncertain write, stale/missing pose, acknowledgement lag, contact release, breakaway, disconnect, identity refresh, and reconnect all fail closed without automatic transfer resumption.
- Contact debounce/hysteresis tests cover timestamp skew, interpolation limits, envelope entry/exit, missing attitude, and stale samples.
- Receiver UI never exposes fuel progress/rate/ETA; tanker UI shows its own detailed accounting and queue.
- Active operation pins its registry snapshot; server restart loses operations but not a valid persisted registry snapshot.
- Test all listed legal and illegal state transitions, including Breakaway, cancellation, and terminal retries with a new operation ID.
- Test fixed and FULL requests, receiver capacity, duplicate commitments, protected reserve, FIFO order, and that distance never reprioritizes the queue.
- Test receiver does not see current fuel, requested/planned/transferred/remaining amounts, flow, ETA, percentage, or progress; test KG/LB changes display only.
- Test 20 Hz active AAR sampling does not increase the normal global traffic polling cadence and does not use WPF timer timing.
- Test operation pinning survives loss of normal spatial interest and is removed on terminal state.
- Test simulator aircraft change, fuel-status timeout, server restart, and identity change stop an operation safely.

## Implementation milestones

1. **AAR-1 — Cloud registry foundation:** schema proposal implementation, validation, super-admin management, sources, draft/publish, audit, and published endpoint.
2. **AAR-2 — TacticalLink module architecture:** generic module routing, versioned AAR protocol shell, registry loader, persistent LKG, and atomic registry activation.
3. **AAR-3 — MSFS fuel integration:** generic SimConnect fuel adapter, current fuel/capacity, safe local writes, readiness, and application acknowledgements.
4. **AAR-4 — Request workflow:** tanker mode, user reserve, discovery, receiver request, FIFO queue, accept/reject, commitments, and KG/LB presentation.
5. **AAR-5 — MSFS pose and geometry:** high-rate pose, pitch/bank, bounded history, timestamp alignment, body-frame geometry, and configurable contact window.
6. **AAR-6 — Contact state:** pre-contact, explicit clear-contact, capture/release, debounce/hysteresis, disconnect, and breakaway.
7. **AAR-7 — Fuel transfer:** per-side effective flow, cumulative authoritative transfer, client application, acknowledgements, ETA, capacity and reserve enforcement.
8. **AAR-8 — Lifecycle hardening:** reconnect, stale data, identity/simulator changes, registry pinning, interest pinning, failure handling, and end-to-end integration tests.
9. **AAR-9 — v0.16.0 release preparation:** only after the preceding acceptance gates pass; release/version work is outside this design task.

## End-to-end acceptance scenario

The eventual v0.16 acceptance run connects human tanker and receiver participants to TacticalLink; resolves their authenticated designators against one published registry version; verifies fresh MSFS pose and usable fuel adapters; joins the tanker, configures reserve, and makes it Available; discovers it from the receiver; submits a fixed or FULL request; queues and accepts it; computes a capacity/reserve-bounded plan; guides the receiver through pre-contact; requires tanker CLEAR CONTACT and stable geometry; then transfers at the minimum of the separately resolved per-side limits. The tanker sees detailed fuel accounting while the receiver sees operational status only. Both simulator adapters apply cumulative deltas and acknowledge them. Contact loss, stale data, write failure, acknowledgement lag, disconnect, or BREAKAWAY immediately stops transfer; reconnect alone never restarts it. Completion does not cross receiver capacity or protected reserve, closes the operation, and returns the tanker to Available unless its pilot selected another state.

## Out of scope for v0.16

Probe/drogue operations, a dedicated boom operator, manual boom control/animation, hose physics, multiple simultaneous receiver contacts, AI tanker/receiver, full internal aircraft fuel-system simulation, real-world clearance enforcement, and persistent active operations are explicitly excluded. Registry profiles may describe future probe/drogue systems, but v0.16 runtime remains boom-only.

## Initial source notes

- VATSIM `aircraft_short` is the ICAO type designator field: [VATSIM Data API](https://vatsim.dev/api/data-api/get-network-data/).
- ICAO Doc 8643 defines aircraft type designators: [ICAO designators and indicators](https://www.icao.int/operational-safety/Designators-and-indicators).
- FAA JO 7360.1K supports the KC-46/KC-767 `B762` and KDC-10 `DC10` mappings: [FAA current type designators](https://www.faa.gov/documentLibrary/media/Order/FAA_Order_JO_7360.1K_Aircraft_Type_Designators.pdf).
- KC-135 tanker boom and adapter methods: [USAF KC-135 fact sheet](https://www.af.mil/About-Us/Fact-Sheets/Display/Article/1529736/kc-135-stratotanker/).
- KC-10 boom and hose/drogue methods; its published gallons-per-minute data is not converted to mass flow without a documented density basis: [USAF KC-10 fact sheet](https://www.af.mil/About-Us/Fact-Sheets/Display/Article/104520/kc-10-extender/).
- A330 MRTT boom/drogue capability and published maximum boom offload 3,600 kg/min (60 kg/s): [Airbus A330 MRTT](https://www.airbus.com/en/products-services/defence/military-aircraft/a330-mrtt). Treat the rate as a sourced A330 MRTT maximum, not an A332-family guarantee; provenance and shared-designator caveat remain visible.
- Receiver registry seed entries and their boom/probe methods require source verification before publication. Do not seed an unverified receiver type or fabricate a flow limit.
