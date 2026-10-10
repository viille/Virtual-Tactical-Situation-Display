# Local AAR fuel tester

This standalone utility is kept out of the main solution and VTSD release. It connects to MSFS SimConnect, sends the same `VTSD_AAR_REQUEST` CommBus messages as VTSD, and displays the WASM bridge's fuel read-back.

It can inspect capabilities and apply a confirmed `+/-` fuel change from 0.1 to 5000 kg over 1 to 600 seconds to the user aircraft. The change is split into at most 30 sequential requests; every step must return a stable, directionally correct simulator read-back before the next step starts. It uses the first write method advertised by the bridge, including the aircraft-native refuel controller on Headwind A339X/FBW aircraft. A partial or failed step stops the transfer. STOP cancels between steps, after the current write has returned its read-back. It changes the running simulator aircraft's fuel. Use it only for local simulator testing.

The utility tests the SimConnect CommBus, AAR WASM bridge, fuel setter and read-back. It does not simulate another Tactical Link participant or test the network AAR queue/operation protocol.

Build locally with the MSFS 2024 SDK:

```powershell
dotnet publish src\LocalAarFuelTester\LocalAarFuelTester.csproj -c Release -r win-x64 --self-contained false -o artifacts\local-aar-fuel-tester-framework -p:MSFS2024_SDK="F:\MSFS2024SDK" -p:PublishSingleFile=true
```

This small build needs the .NET 8 Windows Desktop Runtime. Run `VTSD.LocalAarFuelTester.exe` while MSFS is running and the VTSD AAR Bridge is installed and loaded. If the bundled `SimConnect.dll` is not the one used by VTSD, set `MSFS_SIMCONNECT_DLL` or enter the path to the MSFS 2024 SDK SimConnect DLL in the window.
