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
  names any sim var the sim never sends. **The 12 steps are run again (`--run gate3b`, about 5 minutes)
  before anything from them goes into the profile.**

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

## Realigned, to be confirmed by `gate3b`

- **FCU push/pull (CRO-32):** the knob vars show both directions: SPD, HDG and V/S 0 → 1 → 0, ALT −2 → −1 → −2,
  while the managed lamps and dashes follow (pull: selected, push: managed). Pulling HDG also took the ALT
  managed dot off (expected on the A320: the vertical mode reverts). If `gate3b` shows the same, the hidden
  direction is visible and CRO-32 can sync the push/pull; YourControls is not needed.
- **Flaps:** `S_FC_FLAPS` 0 … 4. The sim's own handle (`A:FLAPS HANDLE INDEX`) only went 0, 1, 2 and back to
  0: Fenix does not keep it in step with its lever. No other candidate moved.
- **Spoilers:** `A_FC_SPEEDBRAKE`: 1 = RET, **0 = ARM** (new: the profile range is 1 … 3), 1.5 … 3 when
  pulled. The sim's spoiler handle and armed state did not move.
- **RUD TRIM:** the switch is 0/1/2, rest 1; the trim shows in `N_FC_RUDDER_TRIM_DECIMAL` (down to −20 in this test, in steps of 1). The
  sim's rudder trim did not move. RESET is a +1/+1 counter (gate test 2 saw it differently).
- **Cockpit door:** the pedestal switch is 0/1/2, rest 1 (`B_DOORS_COCKPIT_LOCKED` follows); the door
  `S_COCKPIT_DOOR` went 1 → 0 → 1 with `I_PED_COCKPIT_DOOR_U`. No sim interactive point moved.
