# profile-probe (CRO-8 step 2)

Probes the draft Fenix A320 profile in the running sim, one var at a time, and writes
proposals only. `profiles/fenix-a320/profile.yaml` is never written.

## Setup (confirm before any sim run)

MSFS 2024, Fenix A320 as the first aircraft, at a gate, engines off, parking brake set,
BAT 1+2 and EXT PWR on, not connected to VATSIM/IVAO, no GSX service running.

## Commands

```
dotnet run --project tools/profile-probe -- <command>
```

| command | what | writes to the sim |
|---|---|---|
| `plan` | what each phase would probe and skip | no (no sim) |
| `readonly` | 30 s noise sample of every watched var, TITLE, fuel datarefs, AIRAC GraphQL candidates | no |
| `verified` | brief item 1 + 4: `[0,1]` guesses, also tries 2; which lamp half reacts | yes |
| `buttons` | item 2: momentary vs latching | yes |
| `positions` | item 3: steps 0..7, keeps only positions a source of truth confirms | yes |
| `encoders` | item 5: +1/-1, which `N_` var follows | yes |
| `breakers` | item 6: the 11 sampled `E_CB_` | yes |
| `auto` | verified, buttons, positions, encoders, breakers back to back | yes |
| `takeover --side capt\|fo` | supervised a: press 1 s, release | yes |
| `hold --var S_FCU_EFIS1_ND_MODE --value 2` | supervised b: hold an ND value (max 120 s), `release` ends it | yes |
| `label --var V --value N --text LABEL` / `info --var V --text T` | record what the pilot saw / a manual finding | no |
| `restore` | restore a var an interrupted run left pending | yes (restore only) |
| `peek NAME... [--clear]` | read vars and the pending var's effect check; `--clear` drops the pending entry only if every effect var is back | no |
| `undo --var NAME` | write one var back to the original value the probe recorded for it | yes (restore only) |
| `propose` | `probe-results.csv`, `probe-report.md`, `profile.proposed.yaml` | no (no sim) |

Options: `--only NAME`, `--redo`. Exit codes: 0 ok, 1 error, 2 safety stop, 3 not restored, 4 Ctrl+C.

## Safety

- The adapter is the bridge's unchanged `SimConnectAdapter`. It gets an in-memory profile in
  which only the current phase's targets are writable, each with only the values that phase
  writes. Its `SimSafetyGuard` refuses everything else.
- Never written: the deny list (`probe-deny.yaml`), the extra skips in `Targets.cs` (APU START,
  EXT PWR, IR modes, door handles, thrust levers, fire-adjacent tests), the 345 non-sampled
  breakers. Flaps are only moved when GraphQL shows no hydraulic pressure.
- A separate read-only SimConnect client (`SimvarReader.cs`) reads SIM ON GROUND, parking brake,
  ground speed, N1 and combustion. Before each var and after each write: if any changes, or
  BAT 1/2, EXT PWR, ENG MASTER or the Fenix parking brake changes, the var is restored and the
  run stops.
- Per var: original recorded, checkpoint written before the first write, probe, restore,
  restore verified (value and effect vars, re-checked a moment later). Ctrl+C or any error:
  restore first, then stop. A var that could not be restored stays pending in
  `profiles/fenix-a320/.probe/checkpoint.json` and is restored on the next start.
- Progress is checkpointed per var, so a run resumes where it stopped. Only one probe run can
  talk to the sim at a time (`run.lock`).

## Lessons from the first run (2026-11-01)

See `profiles/fenix-a320/probe-report.md`, section Surprises. In short:
- Many Fenix keys are press counters (+1 press, +1 release): writing the old value back is no
  press. The restore now tries one more press and marks such vars `counter-suspected`.
- Some keys work as a group (audio transmit, RMP radio keys, EFIS ND options, clock CHR/RST) or
  cannot be undone by the same key (FCU push/pull, FD, rudder trim). They are skipped.
- BUS TIE OFF and the GPU plug cut ground power; ground equipment is skipped. Any change of
  AC/DC bus power is a safety stop, also during the waits inside a test.
- Stepping a switch to a position that does not exist (S_COCKPIT_DOOR 3..7) most likely crashed
  the Fenix WASM. A future position test must not step beyond what a source of truth allows.
- Model animation/render vars (A320_*, A32X_*) move on their own and never count as evidence.
- The pilot's EFIS sync option (single-pilot use) changes what EFIS keys do: turn it off first.
