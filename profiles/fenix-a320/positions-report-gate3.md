# Positions report – gate test 3 prep (2026-11-03)

Read-only run, 2026-11-03 18:20, `profile-probe positions --run gate3`. Fenix A320 IAE (3.0.0.7613) at the
gate, external power. The owner moved each control while the tool recorded every value each sim frame
(44,961 frames, longest gap 73 ms). Nothing was written to the sim. A Chrome HAR of the Fenix EFB was saved in
the same session to `efb-capture/` (git-ignored, never committed).

- Data: `positions-results-gate3.csv` (one row per control and var), `positions-changes-gate3.csv` (every change,
  realigned, see below). The raw files are in `.probe/` (`positions-*-20261103-182002.csv`).
- Evidence in `overrides.yaml`: "pilot input, positions run 2026-11-03".

## A reader error, and what it means for this run

The tool read the new sim vars (`A:` names) in the same 64-slot SimConnect definitions as the Fenix vars.
SimConnect rejected one of them (`ANTISKID BRAKES ACTIVE` or `NOSEWHEEL LOCK ON GROUND`, slot 14 or 15 of
the first block) and dropped it from the definition. From there on, every value of that block was stored
under the name of the var before it, and the last name of the block got garbage.

- Affected: the first block, the steps FLAPS … BARO STD (captain). The other two blocks are correct: the
  setup check reads ENG MASTER and the bus power from the third block, and would have stopped the run if it
  had been shifted; the N1 MODE guards and the RMP keys line up exactly in the second.
- The shift is exact and consistent (for example the "nosewheel" column moves exactly when RUD TRIM is
  turned, and each "slot index" column moves in the next knob's step), so the changes are realigned in
  `positions-changes-gate3.csv`. The 12 shifted steps are left out of `positions-results-gate3.csv`.
- Fixed in the tool: each sim var now has its own definition, so a rejected name shifts nothing, and the run
  names any sim var the sim never sends. **The 12 steps were run again (`--run gate3b`, below)
  before anything from them went into the profile.**

## Clear results (blocks 2 and 3)

| Control | What it is | Proposal |
|---|---|---|
| ENG 1/2 N1 MODE | 0/1 latch, lamp follows (`I_OH_ENG_N1_MODE_n_L`) | `verified [0, 1]`, lamp as check |
| AUTO BRK MED, MAX | +1/+1 push buttons; lamps did not light at the gate | `pulse`, `press: edge`, no lamp check |
| RMP 1 HF1, HF2, AM, NAV, VOR, LS, GLS, ADF, BFO (and VHF1) | +1/+1 push buttons, each lamp follows | `pulse`, `press: edge`, lamp check; RMP 2/3 as the same part |
| Clock SET (`S_MIP_CLOCK_SET`) | +1/+1 push button; the SET knob is the encoder `E_MIP_CLOCK_SET` | `pulse`, `press: edge` |
| Cockpit door video | +1/+1 push button (confirms gate test 2 cause A); lamp did not change | `pulse`, `press: edge` |
| ECAM U/L, PFD/ND brightness (both sides, ND inner and outer ring) | analog in 0.05 steps, seen 0.25 … 0.5; the `N_` display follows at half | `analog`, range to confirm (seen 0.25 … 0.5, likely 0 … 1) |
| DCDU BRT/DIM (`S_DCDU1_DIM_BRT`) | 3-position spring switch: 0 = DIM, 2 = BRT, rests at 1 | `verified [0, 1, 2]`, `rest: 1` |
| ASP 2 VHF 1 volume | analog, seen 0.3 … 0.5 | open item J answered: ASP 2 moves on its own; range to confirm |

**Applied** (owner: apply now): all of the above, through `overrides.yaml`. RMP 2 and 3 keys and the first
officer DCDU follow as the same part; the RMP radio keys get no lamp check (they work as a group, like
VHF1/2/3); brightness range 0 … 1 owner confirmed. Door video is back in the profile as an edge button.

## Cockpit comfort items (CRO-36)

**Applied** (owner decision 2026-11-03: sync now, with the values seen): tray tables, seat switches (rest 1),
armrests, jumpseat and headrest, mask stowage doors `verified [0, 1]` (seats `[0, 1, 2]`); gravity gear
handle `verified [0, 1]` and crank `analog 0 … 3`, both panel priority 2. First officer twins as the same part.
The sunshades stay on the review list.

Profile after this step: added 59, changed 1 (the clock SET knob's label became "CLOCK SET encoder", next to the
new CLOCK SET button). Hash `sha256:938c4671ea4339edff514aeec1c769831d07ee864abdbdb5e07062251563df73`.


| Item | Seen |
|---|---|
| Tray tables | 0/1 each |
| Captain seat (distance, height) | 3-position switches 0/1/2, rest 1; the seat position `N_SEAT_*` follows (0 … 15/16) |
| Armrests | 0/1 each (left captain armrest not moved) |
| Jumpseat, headrest | 0/1 each |
| Oxygen mask stowage doors | 0/1 each |
| Gravity gear | handle 0/1 (unstowed 17 s), crank `S_HYD_GRAVITY_GEAR_EXTEND` 0 … 3 (turns, 0.05 steps) |
| Sunshades | `S_SUNSHADE_CAPT` did not move: wrong var, or the click was not seen |

## Confirmed by the re-run (`--run gate3b`, 2026-11-03 19:06)

The 12 shifted steps again, with the fixed reader (11,576 frames, longest gap 64 ms; data
`positions-results-gate3b.csv`, `positions-changes-gate3b.csv`). The run named the rejected sim var:
`NOSEWHEEL LOCK ON GROUND`. Every realigned reading of the first run was confirmed.

- **FCU push/pull (CRO-32): both directions are visible.** SPD, HDG and V/S: the knob var goes 0 → 1 on the
  pull and back to 0 on the push, and the managed lamp and dashes follow (pull: selected, push: managed). ALT:
  the knob var goes up one on the pull and back on the push (−3 → −2 → −3 here, −2 → −1 → −2 in the first run:
  its resting value moved between the runs, so ALT has an offset, not a fixed rest). Pulling HDG or V/S also
  took the ALT managed dot off (the vertical mode reverts, as on the A320). YourControls is not needed for
  detection. Syncing the push/pull means writing the knob var on the other side: not tested yet (see below).
- **Flaps:** `S_FC_FLAPS` 0 … 4. The sim's handle (`A:FLAPS HANDLE INDEX`) went only 0 → 2 → 0 (first run
  0, 1, 2, 0): Fenix does not keep it in step with its lever. No candidate var explains why a write of
  `S_FC_FLAPS` did not stick in gate test 2.
- **Spoilers:** `A_FC_SPEEDBRAKE` 1 = RET, 0 = ARM, up to 3 when pulled; `B_FC_SPEEDBRAKE` follows (in the first
  run it flickered every frame, here it did not; it is `ignore` either way). The sim's spoiler handle and armed
  state never moved.
- **A/SKID & N/W STRG:** 0/1; `ANTISKID BRAKES ACTIVE` did not move.
- **RUD TRIM:** switch 0/1/2, rest 1; trim display down to −19 … −23; the sim's rudder trim did not move. RESET
  is a +1/+1 counter.
- **Cockpit door:** the pedestal switch is 0 LOCK / 1 NORM / 2 UNLOCK and springs back to 1;
  `B_DOORS_COCKPIT_LOCKED` follows. The door `S_COCKPIT_DOOR` went 1 → 0 → 1 with
  `I_PED_COCKPIT_DOOR_U`: the door was open at the gate (1 = open, 0 = closed, see the write test). No sim
  interactive point moved.
- **BARO STD (captain):** `S_FCU_EFIS1_BARO_STD` 0 → 1 → 0 with the STD flag. In gate test 2 the same var went
  −9 … 1. The two do not fit one rule, so it stays on the review list (the STD flag is compared already).

**Applied** (profile hash `sha256:61b20f924d94224b21eed55d026d5b8248b2b1cef0733fc12447e95d4817db11`):
- `A_FC_SPEEDBRAKE` range 0 … 3 (0 = ARM).
- `S_PED_COCKPIT_DOOR` `verified [0, 1, 2]`, `rest: 1` (LOCK / NORM / UNLOCK); gate test 2 section 5 said it
  should sync.


## Supervised write test (`profile-probe writetest`, 2026-11-03 19:54)

Owner approved. One control at a time, only values seen in the owner's cockpit, Enter before each write, read
back for 2.5 s, every control put back. Raw output: `writetest-results.md`. The owner watched the cockpit:
every control moved as written.

| Control | Written | Stuck | Effect |
|---|---|---|---|
| Flaps lever `S_FC_FLAPS` | 0 → 1 → 0 | yes, both | the lever moved (no hydraulic pressure, so no flaps) |
| Speed brake `A_FC_SPEEDBRAKE` | RET 1 → half 2.0234375 → RET, ARM 0 → RET | yes, all four | the lever moved; `B_FC_SPEEDBRAKE` followed; half lit MASTER CAUTION and ECAM CLR |
| A/SKID & N/W STRG `S_FC_MIP_ANTI_SKID` | 1 → 0 → 1 | yes, both | OFF lit MASTER CAUTION and ECAM CLR |
| Cockpit door `S_COCKPIT_DOOR` | 1 → 0 → 1 (run with `--only DOOR`) | yes, both | the door moved and `I_PED_COCKPIT_DOOR_U` followed. **1 = open, 0 = closed**: the tool had them the other way round (fixed) |
| FCU SPD `S_FCU_SPEED` | own + 1, then own − 1 (0 → 1 → 0) | yes, both | pull: selected speed (managed lamp and dashes off); push: managed again. The owner's counter model holds |

A second run was stopped with Ctrl+C after the flaps went to 1; the restore put them back to 0 (probe log:
restored). The tool now says where a stopped control ended.

**Conclusion for gate test 2 cause H:** written alone in a quiet cockpit, every one of these sticks.

> **Corrected after gate test 3 (CRO-40).** The earlier conclusion here ("something in the receiving cockpit set
> them back, most likely a hardware lever or a sim binding") was wrong. The cause was the bridge: it registered
> one SimConnect data definition per writable var (about 1,240), and the sim refused those late in the list
> without anything in the session log. Writes to those vars (flaps, speed brake, A/SKID, RUD TRIM and RESET,
> cockpit door, the cabin items) never landed. This write test registers only a few definitions, so its writes
> landed. Not Fenix, not hardware, not hydraulics. See [the gate test 3 diagnosis](../../docs/gate-tests/CRO-22-gate-test-3.md),
> section 2A. Fixed in bridge 0.5.0 (a pool of write definitions; a refused write is a red line).

## How each control syncs (applied, profile hash `sha256:968a8d3b9691c0cb0a04c5a5c40099fd564a783c96331c3ef9597f45fc5c09fd`)

| Control | Sync |
|---|---|
| Flaps lever | `verified [0 … 4]` as before; read-back is the check (no lamp; the flaps need hydraulics) |
| Speed brake | `analog 0 … 3` (0 = ARM) as before; read-back is the check |
| A/SKID & N/W STRG | `verified [0, 1]` as before, now with OFF/ON names |
| Cockpit door | `verified [0, 1]`, CLOSED/OPEN names, and the OPEN light `I_PED_COCKPIT_DOOR_U` as its check |
| FCU SPD, HDG, V/S, ALT push/pull | `delta` (the change only: pull +1, push −1 from the receiver's own value), never corrected, one line with its knob. Checked by read-back of the own var, never by a lamp. The existing comparison of the managed lamps and dashes between the cockpits stays a red line after 2 s (ALT included) |

For gate test 3:
- ~~Each pilot should check that no hardware axis or button is bound to flaps or speed brake in the sim that
  receives them.~~ Not the cause (corrected after gate test 3, see above).
- Pull and push each FCU knob once in each cockpit. The gate test 2 prep run saw a push as −1 held while the
  mouse was down; that mirrors as −1, then +1.
