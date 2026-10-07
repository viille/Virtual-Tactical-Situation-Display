# MSFS 2024 AAR Bridge

The v0.16 fuel adapter uses a VTSD-owned standalone WASM package. The bridge only exposes the local aircraft fuel system over MSFS CommBus; TacticalLink queue and operation state remain in the desktop/server domain.

## Build the package

Install the Microsoft Flight Simulator 2024 SDK and its Visual Studio WASM platform toolset, then run from the repository root:

```powershell
$env:MSFS2024_SDK = 'C:\MSFS 2024 SDK'
.
src\MSFS.AarBridge\Build-MsfsAarBridge.ps1
```

The script builds `vtsd_aar_bridge.wasm`, prepares a versioned package with a generated `layout.json`, and stages it under `src/TacticalDisplay.App/Resources/MSFS/vtsd-aar-bridge`. Start the desktop build or publish after this script so the project includes that staged package in `Resources/MSFS` beside the executable. A build without the SDK cannot produce the module; it must not be treated as an AAR fuel capable release.

## Runtime safeguards

- The bridge uses the MSFS 2024 CommBus for `VTSD_AAR_REQUEST` and `VTSD_AAR_RESPONSE`.
- Only the modern `[FUEL_SYSTEM]` path is considered. Legacy fuel systems remain read-only/unavailable.
- Tank quantity and usable capacity are read per index. The bridge refuses writable capability if tank discovery is incomplete, any tank cannot pass a same-value setter/read-back probe, or there are more tanks than the bounded scan can verify.
- Fuel deltas are distributed proportionally to tank headroom for additions and current quantity for removals. Every tank write is read back; a final aggregate read-back determines the actual applied mass. Partial or failed writes must stop the current transfer.
- Duplicate fuel-mutation request IDs return the cached original result. Conflicting payload reuse is rejected. The in-memory cache is limited to 256 entries with a 24-hour TTL; a simulator/module restart clears it, and active operations are not recoverable across that restart.
- The bridge does not use aircraft add-on names as an allow-list. If simulator metadata or writes cannot be verified, `fuel.write` is withheld.

## Manual MSFS 2024 acceptance matrix

These checks require MSFS 2024, the SDK-built package, and a real aircraft. They have not been run in this development environment.

| Check | Expected result | Status |
| --- | --- | --- |
| Install from VTSD UI into detected `Community2024` | Only `vtsd-aar-bridge` is created; sibling packages remain unchanged | Pending |
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
