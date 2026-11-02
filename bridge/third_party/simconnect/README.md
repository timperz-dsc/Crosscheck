# SimConnect DLLs

These two files are **Microsoft's**, not ours:

- `SimConnect.dll` (native client library)
- `Microsoft.FlightSimulator.SimConnect.dll` (managed wrapper)

They come from the **Microsoft Flight Simulator 2024 SDK**, version 1.8.1 (build 2603316),
folder `SimConnect SDK\lib` and `SimConnect SDK\lib\managed`.

They are covered by the MSFS SDK license (EULA), **not** by this repository's MIT license.
They are included only so the bridge can be built on a machine without the SDK (the GitHub
release build). If `MSFS2024_SDK` is set, the build uses the SDK's own files instead.

To update them, copy the same two files from a newer SDK and change the version above.
