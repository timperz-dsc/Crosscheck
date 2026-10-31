# McduReader

Throwaway spike. Reads both Fenix A320 MCDU screens as text from the Fenix web backend, and measures how long a key press made through `L:S_CDU{n}_KEY_*` takes to show up on the screen. See [PROTOCOL.md](PROTOCOL.md) for how the feed works.

## Build and run

Requires Windows x64, the .NET 8 SDK and the MSFS 2024 SDK (`MSFS2024_SDK`), with the Fenix A320 loaded, powered and both MCDUs on.

```
dotnet build -c Release
dotnet run -c Release --no-build -- watch     # read-only: print both screens side by side on every change
dotnet run -c Release --no-build -- probe     # one DATA and one MENU press on CDU1, logs key var and screen
dotnet run -c Release --no-build -- latency   # 10 rounds of MENU/DATA/FPLN/INIT/PROG on CDU1, then CDU2 (~2 min)
```

`latency` presses page keys only. It leaves both MCDUs on the PROG page.

## What the numbers mean

- **Screen grid:** Each screen is a 24x14 text grid: title, 12 lines, scratchpad. Colour and size modifiers are stripped.
- **Hash:** The first 16 hex characters of SHA-256 over the text rows. Colour-only changes do not change it.
- **Latency:** The time from just before the first SimConnect write (the 1) until the GraphQL feed delivers a screen with a different hash. A press is 1, then 0 after 100 ms.
- **fail:** Presses where the screen did not change within 2 s.
- **other CDU changed:** Presses where the other CDU's hash changed during the press and the 1 s settle time that follows.
