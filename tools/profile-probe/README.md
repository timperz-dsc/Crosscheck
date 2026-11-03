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
| `observe [--only ID]` | CRO-29: guided, the pilot clicks push buttons (once, twice fast, 2 s hold) and every change is recorded each sim frame; see below | no |
| `readonly` | 30 s noise sample of every watched var, TITLE, fuel datarefs (AIRAC is not read: preflight asks the pilots) | no |
| `verified` | brief item 1 + 4: `[0,1]` guesses (tries 2 only where 2 is known); which lamp half reacts | yes |
| `buttons` | item 2: momentary vs latching | yes |
| `positions [--run gate3\|gate2] [--only ID]` | CRO-24: guided, the pilot moves each control through its positions (FCU knobs: push, then pull; unsure buttons: one click); every value is recorded and classified; see below | no |
| `encoders` | item 5: +1/-1, which `N_` var follows | yes |
| `breakers` | item 6: the 11 sampled `E_CB_`, 0 and 1 only | yes |
| `auto` | verified, buttons, encoders, breakers back to back | yes |
| `takeover --side capt\|fo` | supervised a: press 1 s, release | yes |
| `hold --var S_FCU_EFIS1_ND_MODE --value 2` | supervised b: hold a known ND value (MODE 0-4, ZOOM 0-5; max 120 s), `release` ends it | yes |
| `label --var V --value N --text LABEL` / `info --var V --text T` | record what the pilot saw / a manual finding (`crash-cutoff`, `crash-end` = UTC times of a Fenix crash and the reload) | no |
| `restore` | restore a var an interrupted run left pending | yes (restore only) |
| `peek NAME... [--clear]` | read vars and the pending var's effect check; `--clear` drops the pending entry only if every effect var is back; `--clear --pilot-ok` also when only effect vars differ and the pilot checked the cockpit (the var itself must be back) | no |
| `undo --var NAME` | write one var back to the original value the probe recorded for it | yes (restore only) |
| `writetest [--only ID] [--bridge-profile]` | supervised write test, Enter before each write, put back afterwards; see below | yes (supervised) |
| `deflimit` | CRO-40: registration only: how many SimConnect data definitions the sim accepts from one client; see below | no |
| `propose` | `probe-results.csv`, `probe-report.md`, `profile.proposed.yaml` | no (no sim) |

Options: `--only NAME`, `--redo`. Exit codes: 0 ok, 1 error, 2 safety stop, 3 not restored, 4 Ctrl+C.

## observe (CRO-29 part 1)

Read only. The tool asks the pilot step by step to click each push button in `ObserveVars.cs`
once, twice as fast as possible, and hold it for about 2 s. Enter = done, `s` = skip, `r` = redo,
`q` = stop and save. It reads every listed L: var each sim frame and logs every change with a
time in ms and the frame number, plus MCDU1 screen changes from Fenix GraphQL.

- It never writes to the sim: it uses its own SimConnect client (`ObserveReader.cs`) that only
  defines and requests data, and never the bridge sim adapter. `ObserveTests` checks this.
- Before the first step it checks the parked setup (on ground, parking brake, engines off,
  external power) and stops if it is wrong; it never fixes anything itself.
- Output: `profiles/fenix-a320/observe-results.csv` (`observe-results-<id>.csv` with `--only`),
  a summary per step in `.probe/observe-summary.txt`, and a timestamped raw copy in `.probe/`.
- At the end it lists lamps and values that are not back at their start value, so the pilot
  can put them back by hand.

## positions (CRO-24, prep for gate test 2)

Read only, same safety as `observe` (same read-only reader, same parked-setup check, same
`ObserveTests` check for write calls). One step per control in `PositionsVars.cs`, most important
for flight first, about 19 minutes in total:

- selectors, switches, levers: "Move <label> through every position and back to where it started"
- FCU knobs: "Push <label>, then pull it"
- push buttons we are not sure of: "Click <label> once"

Enter = done, `s` = skip, `r` = redo, `q` = stop and save. Each step watches the control's var and
its neighbours (so a wrong label still shows which var moved) and classifies every var that moved:

- **positions**: every distinct value, gaps, jumps of more than one position in one frame, whether it
  ended where it started, and how long each value was held (spring-loaded or latching)
- **push button**: only +1 steps (Fenix counts press and release)
- **analog**: fractions or more than 12 values; min..max

Output: `profiles/fenix-a320/positions-results.csv` (one row per control and var),
`positions-changes.csv` (every change, frame by frame), `.probe/positions-summary.txt`, and
timestamped raw copies in `.probe/`. The old write test of this name stays off.

### Runs (`--run`)

- `gate3` (default): the last sim session before gate test 3, 32 steps, about 11 minutes plus the FMGS
  setup. Output files end in `-gate3` (`positions-results-gate3.csv`, ...), so earlier runs stay.
  - The writes that did not stick in gate test 2 (flaps, spoilers, A/SKID, RUD TRIM and RESET, the cockpit
    door switch and the door) also read candidate sim vars (`A:` names in `SimVars.cs`, e.g.
    `A:FLAPS HANDLE INDEX`): the same read-only client reads them with their unit.
  - Before the FCU knobs the tool asks the pilot to set up the FMGS and waits for Enter. Every FCU step
    watches the whole FCU family; its summary says whether push and pull are both visible through some
    var, or that the hidden direction is still not visible (next: how the public YourControls Fenix
    profile detects it, mechanism only).
- `gate2`: the run before gate test 2 (`positions-results.csv`).

## writetest (gate test 3 prep, owner approved 2026-11-03)

Supervised, writes to the sim. `WriteTest.cs` lists the controls and the only values each may get, all seen
in the owner's cockpit: flaps lever 0 → 1 → 0 (only with no hydraulic pressure), speed brake RET → half
(2.0234375, sent in gate test 2) → RET and ARM (0) → RET, A/SKID & N/W STRG 1 → 0 → 1, cockpit door
closed (0) → open (1) → closed, or from open the other way round, and FCU SPD own + 1 then own − 1 from 0 (the owner's counter model; if the step
does not stick or the managed lamp and dashes do not follow, no other value is tried). The door and the
rudder trim stay on the extra-skip list of the automatic phases; only this command writes the door, and
only 0 and 1.

- Before each write it says what it writes and what to watch, and waits: Enter = write, s = skip the rest of
  the control, q = stop. After the write it reads back for 2.5 s: stuck or not, the watched vars before and
  after, any expected lamp, and every other var that changed.
- Each control is put back where it started (the probe restore, also on q or Ctrl+C), and a control that does
  not rest at its start is skipped. It stops at once on a value nobody wrote, when Fenix stops answering, or
  on the usual safety check (on ground, parking brake, engines, BAT, EXT PWR, bus power).
- Results are appended to `profiles/fenix-a320/writetest-results.md`.

### The tray table through the bridge write path (CRO-40, owner approved)

```
dotnet run --project tools/profile-probe -- writetest --only TRAY --bridge-profile
```

`TRAY` is the captain's tray table (`S_TRAY_TABLE_CAPT`), 0 → 1 → 0 (or 1 → 0 → 1), only values seen in the
cockpit; it is cosmetic. In gate test 3 its writes never landed (gate test 3 cause A: one SimConnect definition
per writable var). `--bridge-profile` sets the adapter up exactly like a bridge session: the real profile's watch
list (`SyncEngine.PlanFor`) in read chunks and the pool of write definitions (`SimDefinitions`); the console and
the results file say how many definitions that is. Only the tray table is writable; every other var is watched
read-only, so the guard refuses any other write. It refuses a var that is not a plain switch of the profile, and
it needs no var pending from an earlier run (`restore` first). Same rules as above: Enter before each write,
read back 2.5 s, put back, safety stop on anything unexpected.

## deflimit (CRO-40, owner approved)

```
dotnet run --project tools/profile-probe -- deflimit
```

Registration only. After Enter it opens its own SimConnect client, adds one existing Fenix lamp var
(`I_OH_ELEC_EXT_PWR_L`) to data definitions 0 … 1999 and collects the exceptions SimConnect sends back: the first
refused definition is the limit for one client. Then it clears every definition it made and disconnects. It never
writes a value, never requests data, sends no events and maps nothing (a test fails if such calls appear in
`DefLimit.cs`). The sim at the gate with the Fenix loaded, any state; the bridge need not run. The result is
appended to `profiles/fenix-a320/deflimit-results.md`.

## Range rule

A test tool may only write values that are already known to exist for that var (`KnownValues.cs`):
- its value in the scan,
- a value seen in a valid probe result: the value it rested at, a value written and confirmed by
  an effect, or a value the pilot read on a display,
- a range the owner confirmed: ND MODE 0-4, ND ZOOM 0-5, sampled breakers 0/1, takeover 0/1.

Results from while the Fenix WASM was crashed (`crash-cutoff` to `crash-end`) do not count.
Counters have no fixed range: encoders and the MASTER CAUTION/WARNING press counters only get
+1/-1 steps from the value just read. A var with too few known values is skipped
(`range rule: ...`). `plan` prints exactly which values each var may get.

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
  the Fenix WASM. The position test is switched off and the range rule above was added.
- Model animation/render vars (A320_*, A32X_*) move on their own and never count as evidence.
- The pilot's EFIS sync option (single-pilot use) changes what EFIS keys do: turn it off first.
