# MSFS 2024 AAR Bridge

The v0.16 fuel adapter uses a VTSD-owned standalone WASM package. The bridge only exposes the local aircraft fuel system over MSFS CommBus; TacticalLink queue and operation state remain in the desktop/server domain.

## Build the package

Install the Microsoft Flight Simulator 2024 SDK, then run from the repository root. If Visual Studio/MSBuild with the MSFS platform toolset is available, the script uses it. Otherwise it compiles and links with the `clang-cl`, `wasm-ld`, WASI libraries, and `MSFS_WasmVersions.a` shipped in the SDK:

```powershell
$env:MSFS2024_SDK = 'F:\MSFS2024SDK'
.
src\MSFS.AarBridge\Build-MsfsAarBridge.ps1
```

The script builds `vtsd_aar_bridge.wasm`, prepares a versioned package with a generated `layout.json`, and stages it under `src/TacticalDisplay.App/Resources/MSFS/vtsd-aar-bridge`. It also creates `vtsd-aar-bridge.zip`; the desktop project embeds this archive into the app assembly and single-file release executable. VTSD extracts it to a temporary directory, validates every listed file and hash, then installs it into `Community2024`. This avoids relying on single-file content extraction being next to the executable. A build without the SDK cannot produce the module; it must not be treated as an AAR fuel capable release.

## Build and SDK verification status

The package was built with the locally installed Microsoft Flight Simulator 2024 SDK at `F:\MSFS2024SDK` using its bundled WASM toolchain. The C++ source compiled and linked successfully, including `MSFS_WasmVersions.a`. This proves source/toolchain compatibility only: the simulator has not loaded the module, and no runtime `fsVarsAVarGet` / `fsVarsAVarSet` result, fuel setter result, aircraft fuel-system behavior, or simulator read-back has been observed. The bridge targets `NEW FUEL SYSTEM`, `FUELSYSTEM TANK QUANTITY`, `FUELSYSTEM TANK USABLE CAPACITY`, and `FUEL WEIGHT PER GALLON`. Do not infer runtime support from compilation or desktop fake-bridge tests.

## Runtime safeguards

- The bridge uses the MSFS 2024 CommBus for `VTSD_AAR_REQUEST` and `VTSD_AAR_RESPONSE`.
- Only the modern `[FUEL_SYSTEM]` path is considered. Legacy fuel systems remain read-only/unavailable.
- Tank quantity and usable capacity are read per index. The bridge refuses writable capability if tank discovery is incomplete, any tank cannot pass a same-value setter/read-back probe, or there are more tanks than the bounded scan can verify.
- Fuel deltas are distributed proportionally to tank headroom for additions and current quantity for removals. Every tank write is read back; a final aggregate read-back determines the actual applied mass. Partial or failed writes must stop the current transfer.
- Duplicate fuel-mutation request IDs return the cached original result. Conflicting payload reuse is rejected. The in-memory cache is limited to 256 entries with a 24-hour TTL; a simulator/module restart clears it, and active operations are not recoverable across that restart.
- The bridge does not use aircraft add-on names as an allow-list. If simulator metadata or writes cannot be verified, `fuel.write` is withheld.

## Manual MSFS 2024 acceptance matrix

These checks require MSFS 2024, the SDK-built package, and a real aircraft. They have not been run in this development environment.

Before every runtime test, enable **data-source debug logging** in VTSD. The diagnostics are written to:

```text
%APPDATA%\VirtualTacticalSituationDisplay\logs\debug.log
```

When built with the MSFS 2024 SDK installed, VTSD bundles that SDK's native SimConnect client so end users do not need the SDK. Runtime prefers any configured or discovered client exposing both required CommBus functions over older clients. The log records the selected DLL and whether `SimConnect_CallCommBusEvent` and `SimConnect_SubscribeToCommBusEvent` were found. If CommBus is unavailable, check the `Loaded SimConnect API` line first; a DLL without both exports cannot support AAR bridge communication.

An MSFS SimConnect internal exception while an AAR CommBus request is pending now fails that bridge request without closing the telemetry session. Inspect the MSFS DevMode **Debug > WASM Debug** window for the `vtsd-aar-bridge` module status and error details if it is not running.

For the first smoke test, inspect the `[MSFS-AAR]` entries and confirm that SimConnect opens, the CommBus response subscription succeeds, `HELLO` returns a matching protocol, `GET_CAPABILITIES` and `GET_FUEL_STATE` succeed, discovered tanks and capacity look plausible, and the same-value write probe either succeeds or records a concrete read-only reason. Then perform a controlled `+10 kg` mutation (or another small amount appropriate to the aircraft), confirm the returned `AppliedKg`, and compare the read-back with the simulator fuel state. Keep the log if any step fails, along with the aircraft used, MSFS build/version, VTSD version and build SHA, and bridge version. Do not rely on screenshots when the log contains the failure details.

| Check | Expected result | Status |
| --- | --- | --- |
| Install from VTSD UI into detected `Community2024` | Only `vtsd-aar-bridge` is created; sibling packages remain unchanged | Pending |
| Single-file release package | Bridge ZIP is embedded in the VTSD executable and can be extracted and installed without files beside the executable | SDK package build, embedded-archive install test, and Release single-file publish pass; simulator runtime pending |
| Install using manually selected folder | Invalid folders are rejected; chosen path persists | Pending |
| Restart MSFS after first install | Package is installed on disk, runtime remains not connected until MSFS loads it | Pending |
| CommBus `HELLO` and protocol match | Bridge version and protocol 1 are reported | Pending |
| Protocol mismatch | Runtime reports mismatch; no write capability | Pending |
| Read fuel state | Per-tank quantity/capacity and total kg are fresh and finite | Pending |
| Receiver `+100 kg` | Tank quantities increase within usable capacities; read-back reports actual kg | Pending |
| Tanker `-100 kg` | Tank quantities decrease without going negative; read-back reports actual kg | Pending |
| Capacity/empty boundary | Applied mass is partial or zero and never exceeds available headroom/fuel | Pending |
| Repeated approximately 10 kg increments | Each successful increment matches simulator read-back; duplicate request ID does not apply twice | Pending |
| Unsupported aircraft fuel setter | Bridge remains read-only and Dry Hookup remains available | Pending |
| Bridge disconnect during AAR | Fuel stops; the operation does not resume automatically | Pending |
| Exit MSFS during AAR | Active operation fails and cannot be reconstructed from quantity alone | Pending |
| Update an installed bridge | Previous package remains intact if staging or validation fails; restart is required to load new module | Pending |
| Uninstall bridge | Only `Community2024/vtsd-aar-bridge` is removed | Pending |
| Two-client tanker/receiver transfer | Receiver gain and tanker loss match mutually acknowledged transferred kg | Pending |

## XP12 Web API adapter

XP12 uses its built-in local Web API (X-Plane 12.1.1 or later); it does not use the MSFS SDK or the MSFS WASM bridge. The API client discovers dataref IDs per simulator session, reads current tank mass and aggregate fuel, and only exposes writable capability after a same-value PATCH/read-back probe. Fuel changes are bounded by the calculated per-tank limits and checked against both tank values and the aggregate after every write. A failed, stale, or inconsistent read-back disables further writing and the current operation must stop.

The adapter currently interprets `sim/aircraft/weight/acf_m_fuel_tot` as maximum aircraft fuel capacity, converts pounds to kilograms, and distributes that capacity using `sim/aircraft/overflow/acf_tank_rat`. X-Plane's developer documentation confirms that `acf_m_fuel_tot` is maximum capacity and that `sim/flightmodel/weight/m_fuel[0..8]` are the per-tank values used to fill tanks; it does not specify the capacity unit or the semantics of `acf_tank_rat` on that page. Therefore the unit and capacity distribution assumptions still require validation against X-Plane's current dataref reference and real XP12 aircraft before enabling this adapter for a release. Runtime capability is withheld if the values fail consistency and read-back checks, but those checks do not replace the outstanding aircraft validation. Sources: [X-Plane fuel system](https://developer.x-plane.com/article/the-x-plane-fuel-system/) and [X-Plane local Web API](https://developer.x-plane.com/article/x-plane-web-api/).

During AAR operation, XP12 ownship pose sampling runs at 10 Hz using its built-in Web API. The adapter refreshes fuel at a lower rate. If initialization, connectivity, freshness, capacity validation, or write/read-back fails, XP12 fuel capability is unavailable; traffic continues. The MSFS SDK installed at `F:\MSFS2024SDK` was used only to build the separate MSFS WASM bridge described above. No XP12 simulator runtime validation has been performed.

XP12 manual acceptance remains pending: API disabled/forbidden behavior, timeout and reconnect handling, fresh 10 Hz pose during active operation, stock and third-party tank layouts, fuel capacity units and per-tank ratios, same-value write probe, bounded fuel writes, aggregate read-back, disconnect during transfer, and fail-closed behavior. Passing desktop fake-API tests does not establish XP12 runtime compatibility.
