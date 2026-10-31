# lvar-scanner

Lists every L: variable that exists in MSFS 2024 while an aircraft is loaded, with current values. The output becomes the source for our own aircraft profiles. Nothing is taken from other people's profile files or from aircraft binaries.

> Do not leave the scanner installed for normal flying. Remove it after scanning.

## Why a WASM module

SimConnect cannot enumerate L: vars; the MSFS 2024 SDK only offers that inside the sim, through the WASM Vars API (`fsVarsGetLVarName`, the replacement for the gauge API's `get_name_of_named_variable`). So the scanner has two parts:

- `module/` – a minimal standalone WASM module (`lvar_scanner.cpp`, ~200 lines) that walks all L: var ids and sends names and values to a client through SimConnect client data.
- `LvarScanner/` – a .NET 8 console tool that asks the module for a scan, classifies the result and writes `profiles/fenix-a320/scan/lvars-<date>.csv` plus a summary `README.md` next to it.

**The module is strictly read-only.** It only calls `fsVarsGetLVarName` and `fsVarsLVarGet`. It never sets, registers or creates an L: var, and it never triggers events. `build.ps1` refuses to build if the source mentions any write, register or event call, and the console tool checks that the var count is the same before and after scanning.

## Toolchain

Nothing beyond the MSFS 2024 SDK and the .NET 8 SDK. The module is built with the LLVM that ships in the SDK (`%MSFS2024_SDK%\WASM\llvm\bin\clang-cl.exe` and `wasm-ld.exe`, wasi-sysroot, `MSFS_WasmVersions.a`), so Visual Studio and the MSFS platform toolset are not needed.

## Build

```
powershell -ExecutionPolicy Bypass -File module\build.ps1     # -> package\crosscheck-lvar-scanner\
dotnet build -c Release LvarScanner
```

## Install, scan, remove

1. With the sim closed, copy `package\crosscheck-lvar-scanner` into `C:\Microsoft Flight Simulator 2024\Community`.
2. Start the sim and load the aircraft. For the cleanest list, make the Fenix the first aircraft after a sim start, because L: vars from earlier aircraft stay for the rest of the session.
3. Run the scan:
   ```
   dotnet run -c Release --project LvarScanner -- --samples 3 --interval 2 --verify 20
   ```
   `--coverage <profile.yaml>` additionally reports how many of a profile's L: names exist. It prints only the count; no names from that file are written anywhere.
4. **Remove:** with the sim closed, delete `C:\Microsoft Flight Simulator 2024\Community\crosscheck-lvar-scanner`.

## Protocol

There are two client data areas, both created by the module. All messages are NUL-terminated text.

| area | size | direction | messages |
|---|---|---|---|
| `CROSSCHECK.LVARSCAN.CMD` | 256 B | client → module | `PING <seq>`, `SCAN <seq>`, `ACK <seq> <chunk>` |
| `CROSSCHECK.LVARSCAN.DATA` | 8000 B | module → client | `PONG <seq> <version> <count>`, `CHUNK <seq> <index> <total> <count>` followed by records `<id>\t<name>\t<value>` |

A `SCAN` takes one snapshot in a single frame. The module then sends it chunk by chunk, and the next chunk goes out only after the client's `ACK`. The module walks ids from 0 and stops after 2000 consecutive ids without a name, because ids can have gaps after LVar resets.

## Output

- **CSV columns:** `name, value, prefix (S_ E_ A_ N_ I_ CB_ other), class, class_reason`. The header comment lines record the scan time, MSFS version, Fenix package version (from its `manifest.json`), Fenix app version (from the EFB's `GetSoftwareVersion` query) and scanner version.
- **`class` is a first guess**, to be curated by hand:
  - `observe`: I_, N_, B_
  - `delta`: E_
  - `analog`: A_
  - `pulse`: pushbutton names
  - `verified`: other S_, covers, CB_
  - `stream`: the value changed between scans
  - `unknown`: anything else
- **Caveat:** an L: var exists only once something has created it. Controls Fenix creates lazily may be missing until they have been used.
