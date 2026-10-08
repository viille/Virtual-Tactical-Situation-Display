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

Server-originated `MODULE_EVENT` messages use the same module and version fields, plus `kind`, optional `operationId`, platform `transportSequence`, and a module-owned payload. An operation event also carries its authoritative `OperationRevision`. `transportSequence` orders a transport stream and may reset on reconnect; it is not operation-state authority. Unknown module/version/message kinds are rejected without terminating the TacticalLink connection. Clients report that AAR is unavailable when the server does not support its module protocol. The hub performs common authentication, framing, rate limiting, and participant routing; an AAR handler owns AAR validation and state transitions.

Example operation event:

```json
{
  "type": "MODULE_EVENT",
  "module": "aar",
  "moduleProtocolVersion": 1,
  "transportSequence": 942,
  "kind": "TRANSFER_PROPOSAL",
  "operationId": "operation-id",
  "operationRevision": 20,
  "payload": { "proposedTransferredKg": 5.0 }
}
```

## Reconnect-safe command idempotency and event ordering

The idempotency key is `(ParticipantId, Module, MessageId)`. Connection generation is excluded from the key; it remains authorization and diagnostic context, and stale connections cannot issue new actions. Thus a retry over a newly authenticated connection after reconnect still resolves to the original logical command. The server stores a canonical request hash over module, kind, operation ID, and normalized payload with the original command result. The same key plus the same request returns the original result; the same key with a different request hash returns `IDEMPOTENCY_CONFLICT` and performs no action. A retried accepted command returns its original acceptance result, while the client separately obtains the latest operation snapshot/revision so the replay cannot roll current state backwards.

Retain command results for 24 hours after a completed non-operation command. A `REQUEST_REFUEL` result remains while its request is Pending; if accepted, retain it for the associated operation's lifetime plus 24 hours after terminal state; if rejected/cancelled, retain it for 24 hours after that terminal result. Other operation command results remain for the operation's lifetime and 24 hours after terminal state. Bound storage to 256 unexpired entries per participant and 32,768 entries globally. Expired entries are removed; active records are never evicted early to make room. Look up existing keys before capacity checks so a retry always replays its retained result. At a participant/global cap, reject only a new state-changing command with retryable `IDEMPOTENCY_CAPACITY` until space expires. Store bounded canonical hashes and compact command results, not unbounded event histories. The cache is in server memory; a TacticalLink server restart ends active operations and does not promise replay across that restart. Idempotency retention is a server-side retry guarantee, not persistence of the Pending request or operation itself across a server restart.

Each accepted AAR operation starts `OperationRevision` at 1. Every authoritative mutation or operation event increments it, including state transitions, suspension/recovery, transfer proposals/results, and terminal/safety events. Clients keep `LastSeenOperationRevision` per operation and discard an event with `revision <= LastSeenOperationRevision`. An authoritative snapshot older than the client's last seen revision is also discarded. An identical same-revision duplicate is safely ignored; conflicting content at the same revision is a protocol error and triggers a fresh snapshot, with fuel flow held at zero until reconciled. Process events serially per operation so a newer safety event invalidates queued older transfer work before local application.

`OperationRevision` orders operation state and events. `TransferredKg` is a distinct monotonic cumulative mass value and is never used as the event-ordering token. The generic transport sequence orders only its transport stream. If `revision 21 BREAKAWAY` is processed before an in-flight `revision 20 TRANSFER_UPDATE`, revision 20 is discarded and cannot be applied. A transfer update that was already successfully applied before the newer breakaway was received remains applied; no later application may occur after the safety revision is accepted.

The implementation boundary is a generic module router/handler interface, an AAR domain/state-machine service, and an in-memory operation manager. The server authorizes capabilities from signed `aircraft_type` plus the current published registry snapshot. Local adapter readiness may restrict use further but can never grant registry capability.

## Supported operation and state

V0.16 supports a generic simulator AAR procedure for human-controlled tankers and receivers, one Active receiver per tanker, at most one additional Accepted/Committed receiver, and an in-memory FIFO queue of Pending requests. Registry Boom, Drogue, Probe, and Receptacle methods remain metadata; physical method compatibility is not an eligibility gate because v0.16 does not model the hardware. Dedicated operator controls, hose physics, multiple simultaneous active receiver operations, AI participants, complete simulator fuel-system simulation, and persistence of active operations are out of scope.

Tanker availability is `Off`, `Available`, `Busy`, or `Unavailable`. The pilot explicitly joins as tanker, then selects Available; Busy is derived from an active operation. After an operation, availability returns to Available unless the pilot selected Unavailable or left tanker mode. Receiver has no persistent role mode. Discovery requires a connected peer, tanker registry capability, local adapter readiness, Available state, and normal TacticalLink interest. Method compatibility is not checked in v0.16. An active operation pins both participants into interest regardless of normal radius.

Keep queue requests separate from operations. `PendingRequests` contain requests not yet accepted: they are FIFO-ordered by `RequestedAt`, reserve zero tanker fuel, and can be accepted/rejected by the tanker. Distance never reorders them. A request records request ID, authenticated requester, requested fixed kg or `FULL`, requested time, and current status. Do not create a committed operation until ACCEPT succeeds.

The tanker may have one Active operation and one Accepted/Committed next operation. At most two accepted commitments may exist across the tanker: an Active operation and a `CommittedNext` operation. If there is no Active operation, an accepted request fills the Active slot in `Accepted`; otherwise it fills the single `CommittedNext` slot. If both slots are occupied, another ACCEPT is rejected and the request remains Pending. All other requests stay Pending in FIFO order until the tanker explicitly accepts or rejects them. There is no automatic acceptance. On Active completion, the existing CommittedNext operation is promoted to Active without changing its operation ID or pinned profile snapshot. The tanker may then accept a further pending request into the free committed-next slot.

At ACCEPT time the server recalculates against current tanker fuel, protected reserve, existing accepted commitments, receiver free capacity, and request amount. `FULL` is resolved from current receiver free capacity then. Planned transfer is bounded by request, capacity, and available-to-promise; if no positive safe amount is available, ACCEPT fails and the request remains Pending. Pending requests contribute zero commitment. Only accepted Active and CommittedNext operations reserve fuel:

```text
outstandingCommitmentKg = sum(
    max(0, plannedKg - transferredKg)
    for accepted Active and CommittedNext operations
)
```

The tanker queue is displayed in distinct groups:

```text
ACTIVE RECEIVER
NEXT / COMMITTED
PENDING REQUESTS
```

For example, with two accepted slots occupied, the queue can show:

```text
ACTIVE: VIPER11
NEXT / COMMITTED: VIPER12
PENDING FIFO: VIPER13, VIPER14, VIPER15
```

Only the pending rows offer `ACCEPT` and `REJECT`; the tanker cannot accept another request until the CommittedNext slot is free. Finishing VIPER11 promotes VIPER12, but does not automatically accept VIPER13.

Pending rows have `ACCEPT` and `REJECT`; a committed-next row has `CANCEL COMMITMENT` instead of ACCEPT. A receiver may cancel its own Pending request or CommittedNext operation. The tanker may reject a Pending request or cancel CommittedNext before promotion to Active. Once promoted to Active, cancellation uses normal `DISCONNECT`/`BREAKAWAY` operation semantics. Releasing a cancelled/rejected commitment immediately recomputes available-to-promise.

Receiver requests a fixed kilogram amount or `FULL` (receiver capacity minus current fuel). Tanker explicitly accepts or rejects. The server controls the Active/Committed operation state machine:

```text
Accepted -> Astern -> ClearedContact -> Contact
         -> Refueling -> Disconnecting -> Complete
         -> Suspended -> Astern (Active recovery)
         -> Suspended -> Accepted (CommittedNext recovery)
         -> Suspended -> Disconnecting -> Complete (reconciled plan satisfied)
         -> Cancelled | Breakaway | Failed
```

Legal transitions are constrained: Accepted may become Astern only after tanker `CLEAR_ASTERN`, or become Suspended, Cancelled, or Failed; Cancelled is legal only while the operation is still CommittedNext, before promotion. Astern may become ClearedContact only after tanker `CLEAR_CONTACT`, or become Disconnecting, Suspended, Breakaway, or Failed; ClearedContact may become Contact, Disconnecting, Suspended, Breakaway, or Failed; Contact may become Refueling, Disconnecting, Suspended, Breakaway, or Failed; Refueling may become Disconnecting, Suspended, Breakaway, or Failed; Disconnecting may become Suspended, Complete, or Failed. Suspended Active may become Astern after successful reconciliation or Disconnecting only when the reconciled plan is already satisfied. Suspended CommittedNext may return to Accepted and wait there until promoted, become Cancelled if the receiver/tanker cancels that unstarted commitment, or become Failed. Failed reconciliation becomes Failed. Rejected and Cancelled request records are terminal; Cancelled, Breakaway, Complete, and Failed operation states are terminal. `Suspended` is non-terminal. Once promoted to Active, use Disconnect/Breakaway semantics rather than Cancelled. No other transitions are legal. A retry after a terminal result creates a new request/operation ID.

Clients send intent/actions; only the server advances authoritative operation state. Repeated commands with the same `messageId` return the original outcome and do not create another request, commitment, or transfer. On a duplicate, publish/return the current request or operation snapshot separately so a stale command result cannot overwrite a newer revision.

## Fuel model and transfer safety

All canonical amounts are kilograms. UI preference may display kilograms or pounds using `1 kg = 2.2046226218 lb`; changing units never alters operation values. `FULL` is resolved at request acceptance from receiver capacity minus current fuel. A protected tanker reserve is operator-configurable and has no hard-coded military policy. Tanker accounting exposes `CurrentFuelKg`, `ProtectedReserveKg`, `CommittedFuelKg`, and `AvailableToPromiseKg`.

At ACCEPT time, planned amount is bounded by the request, freshly reported receiver free capacity, and freshly calculated tanker fuel available to promise. The accounting invariant is:

```text
outstandingCommitmentKg = sum(
    max(0, plannedKg - transferredKg)
    for accepted Active and CommittedNext operations
)
pending requests contribute 0 kg
availableToPromiseKg = max(0, currentTankerFuelKg
                              - protectedReserveKg
                              - outstandingCommitmentKg)
```

Transferred fuel is removed from current fuel and also reduces the outstanding commitment, so it is never subtracted twice. The server locks the accepted plan and effective rate for the operation.

Per-side limits preserve provenance: `confirmed aircraft-specific value`, historical `method_fallback`, or `unknown`. For each side, use the minimum confirmed positive aircraft-specific limit across its systems; if none exists, use the generic simulated AAR fallback of 10 kg/s:

```text
tankerLimit = minimum confirmed tanker system limit ?? 10 kg/s generic fallback
receiverLimit = minimum confirmed receiver system limit ?? 10 kg/s generic fallback
effectiveFlowKgPerSecond = min(tankerLimit, receiverLimit)
```

The 10 kg/s fallback is generic VTSD simulated AAR policy, not a confirmed aircraft, Boom, or Drogue value. For profiles with multiple systems and no selected method, use the minimum confirmed positive rate, never the highest rate. The fallback must never be shown as a confirmed type value in either desktop UI or Cloud admin.

Only the server owns monotonic `TransferredKg`, the cumulative AAR mass confirmed as successfully applied by both participants. Engine burn remains independent and is never inferred as AAR transfer. To avoid counting a requested write as completed transfer, a server `TRANSFER_PROPOSAL` carries a proposed cumulative target but does not itself advance `TransferredKg`. Clients apply the difference between that target and their `LastAppliedTransferredKg`; receiver adds and tanker removes fuel. The server commits the proposed target to `TransferredKg` only after both clients acknowledge the same actually applied cumulative mass (within the adapters' declared mass resolution). A transfer proposal and its result are operation events and are ordered by `OperationRevision`.

The fuel adapter must return an `AppliedFuelResult` equivalent to `{ requestedKg, appliedKg, outcome: Success | Partial | Failed }`, or provide a post-write measurement that isolates the AAR change safely. Total simulator fuel delta alone is not sufficient when engine burn/refill can occur concurrently. The client advances `LastAppliedTransferredKg` and acknowledges only actual applied AAR mass, never the requested amount. For example, if a +5 kg proposal results in only +3 kg at the receiver, its acknowledgement advances by 3 kg, not 5 kg.

Only one bounded transfer proposal may be unresolved per operation. If either participant is materially behind its proposal, reports Partial/Failed, or reports a cumulative value that disagrees with the other side, immediately pause flow and enter `Suspended`; issue no next proposal. Freeze `TransferredKg` while Suspended. Reconcile both actual cumulative application values and current fuel/capacity/reserve. If both sides report the same applied cumulative amount, the server may adopt that common amount only atomically as part of leaving Suspended; it must not advance `TransferredKg` while the operation remains Suspended. Reconciliation may only reduce `PlannedKg` (never below the reconciled `TransferredKg`) to honor newly observed receiver capacity or tanker reserve; it may not increase the accepted plan. If the reduced plan is already satisfied, close through Disconnecting/Complete. If a proposal was only partially applied, both peers must still agree on the same resulting cumulative amount. The partial-write recovery path is distinct from ordinary network reconnect: it may leave Suspended only after successful reconciliation to a safe closed result, or after reconciliation to Astern with a fresh application baseline; the latter requires a new CLEAR CONTACT and stable contact capture before any transfer resumes. It never resumes directly to ClearedContact, Contact, or Refueling. If the peers report divergent cumulative application, fail the operation rather than attempting a compensating write or rollback. If capacity/reserve prevents safe continuation, fail or complete only after the reconciled plan is safely closed. Do not silently continue at the prior commanded rate. This preserves `TransferredKg` as mutually applied AAR mass; a mismatch that cannot be reconciled symmetrically is surfaced as a failed operation, not hidden as completed transfer.

The server pauses if either acknowledgement exceeds its configured lag bound. Stop conditions also include planned amount reached, receiver full, tanker reserve boundary, disconnect, contact loss, stale pose, adapter loss, breakaway, or uncertain write outcome. No action automatically resumes transfer after reconnect.

Relevant fuel status is sent privately to the operation participants at 1–2 Hz; it is not added to global high-rate telemetry. The receiver UI may show request and operational state, but never current fuel, transferred amount, remaining amount, rate, ETA, percentage, or progress bar. The tanker UI may show current/reserve/committed/available fuel, active receiver, request/planned/transferred/remaining amounts, flow/ETA, contact state, queue, and operator actions.

The tanker control surface includes `JOIN AS TANKER`, `LEAVE TANKER MODE`, `ACCEPT`, `REJECT`, `CLEAR ASTERN`, `CLEAR CONTACT`, `START_TRANSFER`, `STOP_TRANSFER`, `DISCONNECT`, and immediate `BREAKAWAY`. Before an operation the receiver may see available tanker, distance, requested onload, `FULL`, and request status. During it, the receiver sees only operational messages (for example request accepted, proceed/hold astern, cleared contact, contact, disconnect, or breakaway). KG/LB is a presentation preference only; there is no liters mode.

## MSFS integration, pose, and contact

V0.16 targets MSFS first. Fuel access is through an `IAarFuelAdapter` contract for current fuel, capacity, write availability, and applying a signed mass delta. The MSFS path is `VTSD -> MsfsAarFuelAdapter -> CommBus -> VTSD WASM AAR Bridge -> MSFS fuel AVars`; fuel quantities are read and applied through the bridge. Unsupported or uncertain writes fail closed; no silent synthetic transfer is allowed.

AAR samples ownship pose on a dedicated asynchronous path independent of WPF dispatch cadence and normal traffic polling. XP12 samples at a 100 ms interval (10 Hz) only while an AAR operation is active, maintains a short timestamped history, and aligns participant samples by timestamp with bounded interpolation/extrapolation. The current pose model uses latitude/longitude/altitude, heading, timestamp, and velocity. It does not use pitch or bank; their absence is not an AAR readiness failure.

Use explicit configuration bounds, with units, for maximum sample age, maximum alignment gap, bounded extrapolation, and telemetry-loss timeout. These are simulator parameters, not real-world contact limits. Global traffic polling remains at its normal cadence; XP12 AAR ownship pose sampling runs at a 100 ms interval while an operation is active. Use a background timer/latest-value path and bounded history rather than WPF timer scheduling or unbounded buffering.

The current v0.16 server compares tanker and receiver aircraft reference positions and velocities in a common local relative frame. It does not use pitch/bank, aircraft-body refueling contact offsets, hose extension, or hardware geometry. These broad envelopes identify a plausible, stable AAR formation region; they do not detect boom/receptacle or basket/probe engagement. The optional registry geometry field remains available for future method-aware work, but does not define physical connection in v0.16. Exact addon/model matching is not required.

Contact capture uses aircraft reference positions and the configured coarse position and relative-velocity envelope continuously for a debounce interval. The current defaults are 12–160 m behind, ±18 m lateral, 2–32 m below, ≤4 m/s relative speed, and 1 second capture debounce. Release hysteresis defaults are 6–210 m behind, ±28 m lateral, 0–48 m below, ≤7 m/s, and 250 ms debounce. These broad bounds tolerate simulator/network update timing and jitter; they do not model boom-tip/receptacle contact, basket/probe engagement, hose extension, nozzle position, or aircraft-specific refueling contact points. `Contact` means stable presence in the coarse server-approved region, not physical hardware engagement. The tanker explicitly executes `START_TRANSFER`; geometry alone never authorizes fuel. Invalid geometry, stale or misaligned poses, excessive relative motion, and failed debounce continue to revoke or prevent contact.

## Suspension, reconnect, and process lifecycle

Distinguish a temporary WebSocket/network reconnect from a desktop process restart. Each desktop process creates a random `ClientInstanceId` once at startup and retains it across reconnects; a new process creates a different value. The server keeps it in the logical participant's reconnect state. It is not an aircraft identity or capability claim.

On temporary TacticalLink loss, immediately stop fuel proposals and transition any accepted/committed operation in Accepted, Astern, ClearedContact, Contact, Refueling, or Disconnecting to non-terminal `Suspended`. Record its `SuspendedFromState`, reason, and fixed deadline. Increment OperationRevision, set effective flow to zero, discard contact validity and prior CLEAR CONTACT, and cancel any unapplied older transfer proposal. An unaccepted Pending request holds no commitment; retain it only through the existing 12-second reconnect grace, then cancel it and release its queue position. Suspended operations retain their accepted commitment during that grace and release it if recovery fails. Entering Suspended invalidates any previous fuel acknowledgement baseline for purposes of restarting flow; recovery must establish a fresh baseline from reconciled actual application state.

Recovery is allowed only before the original 12-second reconnect-grace deadline and only if both participants rejoin the same logical `ParticipantId` with a valid newer connection generation, the same `ClientInstanceId` per process, and the same authenticated identity tuple (`userId`, VATSIM CID, callsign, and signed `aircraft_type`). Both participants must be connected and still compatible with the operation's pinned registry profiles. Require fresh aligned pose, ready adapters, reconciled current fuel/capacity/reserve, and a fresh cumulative application acknowledgement baseline matching actual local application. Connection generation is checked as reconnect/authorization context but never changes command idempotency identity. A participant presenting a different `ClientInstanceId` is a process restart and immediately fails the old operation; it cannot be treated as an eligible reconnect even inside the grace period.

Successful reconciliation of an Active operation returns `Suspended -> Astern` when `SuspendedFromState` was Accepted, Astern, ClearedContact, Contact, or Refueling; it never resumes to ClearedContact, Contact, or Refueling. Contact and clearance are invalidated. Tanker must issue a new CLEAR CONTACT and stable geometry must be captured again before transfer. If the original state was Disconnecting, reconciliation can only finish Disconnecting/Complete and must not restart transfer. A CommittedNext operation returns to `Accepted` and remains in the committed-next slot without contact clearance until promoted. If only one participant reconnects, remain Suspended with zero flow until the other rejoins or the original deadline expires. Grace expiry, identity/capability mismatch, changed client process ID, unavailable adapter, irreconcilable fuel/application state, or failed pose validation causes `Suspended -> Failed` and releases commitments. A partial application suspends immediately and gets a bounded five-second adapter reconciliation window; failure to reconcile within that window transitions to Failed. This short adapter reconciliation deadline does not extend the network reconnect grace period.

A desktop process restart during an operation changes `ClientInstanceId`, so the server transitions the old operation to Failed and never resumes it. The restarted client cannot reconstruct the operation from simulator fuel quantity alone because its local cumulative watermark was lost. Persistent client-side AAR recovery is out of scope for v0.16. Server restart drops all transient requests, commitments, and operations; clients treat a missing operation after reconnect as failed and do not reconstruct it from fuel. The registry LKG cache is independent.

Also stop or prevent flow on missing/stale pose, fuel-status timeout, fuel adapter loss/write failure, application-acknowledgement lag, receiver full, reserve reached, contact loss, simulator aircraft change, identity change, process restart, or uncertain write outcome. The default is `uncertain state -> no fuel flow`. An operation peer relationship pins interest independently of normal spatial-radius membership for the operation lifetime, including suspension and teardown events.

The server validates message identity, operation ownership, legal state transition, registry version, participant capability, and local readiness before acting. Invalid commands return module-scoped errors and do not affect other TacticalLink modules. Rates, queue length, payload size, and per-participant command frequency are bounded. Operational logs include operation ID, state changes, registry version, stop reason, and adapter outcomes; avoid logging secrets or unnecessary personal data.

## Design invariants

1. The same logical client command cannot execute twice because of a reconnect; the idempotency identity excludes connection generation, and reuse with a different command/payload is rejected.
2. `OperationRevision` never decreases, and a client never applies an event older than its last accepted operation revision.
3. A same-revision event is ignored only if its content is identical; conflicting content causes snapshot reconciliation with flow held at zero.
4. No fuel application may occur after the client accepts a newer Breakaway, Failed, Disconnecting, or Suspended safety event.
5. Network interruption immediately sets flow to zero and freezes `TransferredKg`; reconnect alone never resumes flow.
6. `Suspended` is non-terminal, but no Suspended operation produces fuel flow or advances `TransferredKg`; recovery requires reconciliation and a new contact clearance.
7. A changed `ClientInstanceId` means process restart and fails the old operation; v0.16 never reconstructs it from simulator fuel alone.
8. Applied-fuel acknowledgements report actual simulator-applied AAR mass, not requested mass; partial writes pause flow and cannot be silently acknowledged as complete.
9. Pending requests reserve zero fuel; accepted/committed operations reserve only their remaining planned transfer.
10. V0.16 permits at most one Active receiver and one CommittedNext receiver; all other requests remain FIFO Pending until the tanker explicitly accepts or rejects them.
11. Accepting a Pending request re-evaluates current fuel, reserve, commitments, receiver capacity, and request amount.
12. A committed-next operation can be cancelled before promotion; once Active, normal Disconnect/Breakaway semantics apply.
13. Uncertain state always resolves toward zero fuel flow.

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
- Test that one Active plus one CommittedNext fills both accepted slots, an additional ACCEPT is rejected while the request remains Pending, and a freed CommittedNext slot is not filled automatically.
- Test pending requests reserve 0 kg and are excluded from available-to-promise commitment accounting, while each accepted Active/CommittedNext operation reserves `max(0, plannedKg - transferredKg)`.
- Test receiver does not see current fuel, requested/planned/transferred/remaining amounts, flow, ETA, percentage, or progress; test KG/LB changes display only.
- Test 10 Hz active AAR sampling at the XP12 100 ms interval does not increase the normal global traffic polling cadence and does not use WPF timer timing.
- Test operation pinning survives loss of normal spatial interest and is removed on terminal state.
- Test simulator aircraft change, fuel-status timeout, server restart, and identity change stop an operation safely.

### Reconnect-safe idempotency

- Accept a `REQUEST_REFUEL`, drop its response, reconnect with a new connection generation, and retry the same `messageId`; return the original result without a second request or commitment, plus current state through a separately revisioned snapshot.
- Retry the same participant/module/message ID and identical command/payload; return the original command result.
- Reuse a participant/module/message ID with a changed kind or payload; reject with `IDEMPOTENCY_CONFLICT` and make no state change.
- Verify entries expire only according to their TTL/operation retention; live requests and operations retain entries; participant/global capacity rejects new mutations rather than evicting unexpired entries.
- Verify an idempotency retry after reconnect replays the original result, while a server restart ends transient request/operation state and is not covered by cross-restart replay.

### Operation revision and reordering

- Deliver revision 21 `BREAKAWAY` before revision 20 `TRANSFER_PROPOSAL`/`TRANSFER_UPDATE`; discard revision 20 and apply no fuel after Breakaway.
- Deliver stale Contact, Refueling, Transfer, and Disconnect events after a newer state; none may overwrite it.
- Ignore an identical same-revision duplicate, but reject conflicting payloads for one revision and require a fresh snapshot.
- Confirm transport sequence reset on reconnect does not reset operation revision or alter command idempotency.

### Suspended lifecycle and process restart

- Refueling -> network loss -> Suspended; verify flow is zero, TransferredKg stops for the entire Suspended state, and contact/clearance are invalidated.
- Reconnect both same-process participants within the existing 12-second grace; reconcile identity, pose, adapters, fuel, and application watermark; recover only to Astern; require new CLEAR CONTACT and stable capture before Refueling.
- Reconnect grace expiry, identity/capability mismatch, process-ID change, unavailable adapter, or irreconcilable fuel -> Failed and release commitment.
- Restart the desktop process during an operation; its new ClientInstanceId causes the old operation to fail; do not reconstruct from total simulator fuel.
- Verify a process restart is not accepted as a same-process reconnect even when the original reconnect grace remains open; a normal reconnect retains ClientInstanceId and remains eligible for reconciliation.

### Partial fuel application

- Request a local +5 kg adapter write that reports +3 kg applied; acknowledge only +3 kg, pause proposals, enter Suspended, freeze TransferredKg, and reconcile without silently applying/acknowledging the missing 2 kg.
- Test equal partial cumulative application on both peers, divergent peer totals, zero applied, and write failure. Mutually applied mass is adopted atomically only as the operation exits Suspended after reconciliation. Verify flow remains zero throughout suspension, no next proposal is issued, and the operation either closes safely or returns to Astern with fresh acknowledgements, a new CLEAR CONTACT, and stable contact capture. A mismatch that cannot be reconciled without rollback fails the operation.
- Confirm engine burn is not counted as AAR application when deriving any post-write value.

### Queue and commitment semantics

- With one Active, one CommittedNext, and N Pending, reject another ACCEPT while the committed-next slot is full; keep all other Pending requests FIFO and unreserved.
- Verify pending requests contribute zero committed kg, accepted Active/CommittedNext requests reserve their remaining plan, and completion/cancellation releases it.
- Change tanker fuel, protected reserve, receiver capacity, or existing commitments after request submission; ACCEPT must use the latest values and either create a safe commitment or leave the request Pending with an error.
- Verify receiver cancellation of own Pending/CommittedNext, tanker rejection of Pending, tanker cancellation of CommittedNext, and no queue ACCEPT action after promotion to Active.
- Verify only accepted Active and CommittedNext operations count as commitments, pending requests remain FIFO and unreserved, and accepting a pending request rechecks current fuel/reserve/capacity.

## Implementation milestones

1. **AAR-1 — Cloud registry foundation:** schema proposal implementation, validation, super-admin management, sources, draft/publish, audit, and published endpoint.
2. **AAR-2 — TacticalLink module architecture:** generic module routing, versioned AAR protocol shell, registry loader, persistent LKG, and atomic registry activation.
3. **AAR-3 — MSFS fuel integration:** CommBus/WASM bridge to MSFS fuel AVars, current fuel/capacity, safe local writes, readiness, and application acknowledgements.
4. **AAR-4 — Request workflow:** tanker mode, user reserve, discovery, receiver request, FIFO queue, accept/reject, commitments, and KG/LB presentation.
5. **AAR-5 — Pose and geometry:** XP12 100 ms sampling (10 Hz), bounded history, timestamp alignment, aircraft reference-position and velocity geometry, and configurable contact window; pitch/bank and physical contact offsets are not used.
6. **AAR-6 — Contact state:** Accepted, `CLEAR_ASTERN` to Astern, `CLEAR_CONTACT` to ClearedContact, stable geometry capture to Contact, explicit transfer start/stop, disconnect, and breakaway.
7. **AAR-7 — Fuel transfer:** per-side effective flow, cumulative authoritative transfer, client application, acknowledgements, ETA, capacity and reserve enforcement.
8. **AAR-8 — Lifecycle hardening:** reconnect, stale data, identity/simulator changes, registry pinning, interest pinning, failure handling, and end-to-end integration tests.
9. **AAR-9 — v0.16.0 release preparation:** only after the preceding acceptance gates pass; release/version work is outside this design task.

## End-to-end acceptance scenario

The eventual v0.16 acceptance run connects human tanker and receiver participants to TacticalLink; resolves their authenticated designators against one published registry version; verifies fresh MSFS pose and usable fuel adapters; joins the tanker, configures reserve, and makes it Available; discovers it from the receiver; submits a fixed or FULL request; accepts one request as Active and optionally one as CommittedNext while later requests remain FIFO Pending; computes each accepted plan from current fuel/reserve/commitment/capacity values; guides the Active receiver through Astern positioning; requires tanker CLEAR CONTACT and stable geometry; then transfers at the minimum of the separately resolved per-side limits. The tanker sees Active, Next/Committed, and Pending groups plus detailed fuel accounting while the receiver sees operational status only. Both simulator adapters report actual cumulative application; only mutually applied mass advances `TransferredKg`. Contact loss, stale data, partial/write failure, acknowledgement lag, disconnect, or BREAKAWAY immediately stops transfer; reconnect alone never restarts it. A same-process reconnect inside grace reconciles to Suspended and requires new CLEAR CONTACT/capture; process restart fails the old operation. Completion does not cross receiver capacity or protected reserve, promotes the committed-next operation if present, and returns the tanker to Available when no active/committed work remains unless its pilot selected another state.

## Out of scope for v0.16

Physical Boom/Drogue compatibility enforcement, a dedicated boom operator, manual boom control/animation, hose physics, multiple simultaneous receiver contacts, AI tanker/receiver, full internal aircraft fuel-system simulation, real-world clearance enforcement, and persistent active operations are explicitly excluded. F-18 Probe and F-35 BoomReceptacle metadata can both enter the generic v0.16 procedure when profile roles permit. Future versions may add selected systems, a compatibility matrix, and physical-method-specific geometry without removing registry metadata.

## Initial source notes

- VATSIM `aircraft_short` is the ICAO type designator field: [VATSIM Data API](https://vatsim.dev/api/data-api/get-network-data/).
- ICAO Doc 8643 defines aircraft type designators: [ICAO designators and indicators](https://www.icao.int/operational-safety/Designators-and-indicators).
- FAA JO 7360.1K supports the KC-46/KC-767 `B762` and KDC-10 `DC10` mappings: [FAA current type designators](https://www.faa.gov/documentLibrary/media/Order/FAA_Order_JO_7360.1K_Aircraft_Type_Designators.pdf).
- KC-135 tanker boom and adapter methods: [USAF KC-135 fact sheet](https://www.af.mil/About-Us/Fact-Sheets/Display/Article/1529736/kc-135-stratotanker/).
- KC-10 boom and hose/drogue methods; its published gallons-per-minute data is not converted to mass flow without a documented density basis: [USAF KC-10 fact sheet](https://www.af.mil/About-Us/Fact-Sheets/Display/Article/104520/kc-10-extender/).
- A330 MRTT boom/drogue capability and published maximum boom offload 3,600 kg/min (60 kg/s): [Airbus A330 MRTT](https://www.airbus.com/en/products-services/defence/military-aircraft/a330-mrtt). Treat the rate as a sourced A330 MRTT maximum, not an A332-family guarantee; provenance and shared-designator caveat remain visible.
- Receiver registry seed entries and their boom/probe methods require source verification before publication. Do not seed an unverified receiver type or fabricate a flow limit.
