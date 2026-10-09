Fallback location for an official MSFS SDK native SimConnect client DLL:

  SimConnect.dll

Source:
- MSFS SDK / SimConnect SDK / lib / x64

When building with MSFS2024_SDK set, the app project bundles that SDK's client
DLL here in the publish output. Runtime prefers a client with MSFS 2024 CommBus
exports over an older client at this fallback location.
