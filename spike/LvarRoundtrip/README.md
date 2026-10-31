# LvarRoundtrip

Throwaway spike. Measures how long it takes from writing a Fenix A320 L: variable via SimConnect until the new value is read back from the sim.

L: vars are used directly in SimConnect data definitions (`"L:NAME"`, unit `"number"`, `FLOAT64`). The MSFS 2024 SDK documents this under `SimConnect_AddToDataDefinition`, so no WASM module is needed.

## Build and run

Requires Windows x64, the .NET 8 SDK and the MSFS 2024 SDK, with `MSFS2024_SDK` pointing at its folder.

```
dotnet build -c Release
dotnet run -c Release --no-build -- --check   # loads SimConnect without connecting
dotnet run -c Release --no-build              # the measurement (takes ~6 minutes)
```

Load the Fenix A320 cold & dark at a gate before running. The tested switches (nav/logo lights, no-smoking signs, dome light, APU master, BAT 1) toggle visibly in the cockpit, and each one is restored at the end. Ctrl+C also restores them.

## What it does

The app subscribes to all test variables, plus `L:I_MIP_MASTER_CAUTION_CAPT`, at `SIM_FRAME` rate. For each variable it runs 30 iterations. Each iteration writes the opposite value (0 becomes 1, anything else becomes 0), then times how long it takes until the first frame whose data shows the written value. There is a 1 s pause between iterations.

## Reading the numbers

- **readback_ms** is the time from just before `SetDataOnSimObject` until the frame data containing the new value is received by this process. It counts the full round trip seen by a client, so it can't be smaller than about one sim frame (~16–33 ms).
- **fail** counts writes that did not read back within 2 s.
- **revert** counts writes that read back but changed again within the following 1 s pause, for example because the aircraft logic overrode them. A read-back alone shows the value reached the sim's L: var store. It does not show the Fenix accepted the change, so watch the switches as well.
- Every sample is written to `results.csv` (variable, iteration, written, readback_ms, ok).
- The project gate is an overall median below 150 ms.
