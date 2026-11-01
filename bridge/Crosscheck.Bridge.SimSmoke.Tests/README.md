# Real-sim smoke test (CRO-8)

Checks the real `SimConnectAdapter` against MSFS 2024. Skipped by default; `dotnet test`
reports it as skipped.

What it does:

1. Connects through SimConnect (waits up to 60 s for the sim).
2. Reads every var of `profiles/fenix-a320/profile.yaml` (change-only, grouped definitions).
3. Writes `S_FCU_EFIS1_ND_MODE` (captain's ND mode) one step further and checks the read-back.
4. Checks that an out-of-range write is refused before it reaches the sim.
5. Puts the ND mode back to its original value, also if a step fails.

Nothing else is written. No WASM module and nothing in the Community folder is needed.

## Run

1. Start MSFS 2024 and load the Fenix A320 (cold & dark at a gate is fine).
2. In PowerShell, from the repo root:

```
$env:CROSSCHECK_SIM_SMOKE = "1"
dotnet test bridge/Crosscheck.Bridge.SimSmoke.Tests
Remove-Item Env:CROSSCHECK_SIM_SMOKE
```

Watch the captain's ND: it changes mode once and changes back.
