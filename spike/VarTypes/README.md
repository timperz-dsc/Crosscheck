# VarTypes

Throwaway spike with two goals:
- Show that write and read-back work for every kind of Fenix A320 control, not only on/off switches.
- Find out what Fenix's GraphQL backend exposes beyond L: vars.

Results:
- [TYPES.md](TYPES.md): control types, write methods and latencies.
- [GRAPHQL.md](GRAPHQL.md): what the backend exposes.
- [PROFILES.md](PROFILES.md): a comparison of the two Fenix YourControls profiles.

## Build and run

Requires Windows x64, the .NET 8 SDK and the MSFS 2024 SDK (`MSFS2024_SDK`), with the Fenix A320 loaded and powered (batteries and external power). The profile paths are hard-coded in `Infra.cs` (`Profiles`).

```
dotnet build -c Release
dotnet run -c Release --no-build -- indicators     # read every L:I_* from both profiles, list the lit ones
dotnet run -c Release --no-build -- gqlrate 15     # one subscription with 22 datarefs for 15 s
dotnet run -c Release --no-build -- probe L:E_FCU_SPEED --watch L:N_FCU_SPEED --poll system.numerical.N_FCU_SPEED +1 -1
dotnet run -c Release --no-build -- test           # the suite: 10 iterations per control (~7 min)
```

`probe` writes values to one L: var and logs every change it sees in the watched L: vars (`--watch`), in GraphQL subscriptions (`--gql`) and in HTTP polls (`--poll`). Values can be absolute (`2`) or relative to the current value (`+1`, `-1`). Except for `E_` encoders, the original value is restored afterwards.

`test` only touches the controls listed in TYPES.md. For RAT MAN ON and IDG 1 it moves only the guard covers, and it aborts if the switch underneath changes. All values are restored at the end, also on Ctrl+C. Every sample is written to `results.csv`, which is gitignored.

## Reading the numbers

All times are measured from just before the SimConnect write.

- **lvar:** the same L: var reads back the written value.
- **effect:** another L: var shows that the aircraft reacted: `N_FCU_*` for encoders, `I_ECAM_*` for the ECAM page buttons.
- **gql sub / gql http:** Fenix's own GraphQL dataref shows the new state, either pushed by the subscription or seen by polling over HTTP.
