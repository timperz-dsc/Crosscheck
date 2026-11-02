# Positions report – Fenix A320 controls (gate test 2 prep, CRO-24)

Read-only run, 2026-11-02 19:11. Fenix A320 IAE (3.0.0.7613) at the gate, external power.
The owner moved each control while `profile-probe positions` recorded every value each sim frame
(38,952 frames, longest gap 120 ms). Nothing was written to the sim.

- Data: `positions-results.csv` (one row per control and var), `positions-changes.csv` (every change).
- The redos of the four FCU knobs are not in the saved files (only this run was saved). The saved
  data shows the same as the redos: only -1.
- Evidence in `overrides.yaml`: "pilot input, observe positions run 2026-11-02".

## Result in one line

58 of 66 controls gave a clear answer (4 did not move or were skipped, 4 are the FCU knobs). Every selector, switch and lever showed positions with no
gap and ended where it started. Every button we were unsure of was either a +1/+1 counter or a 0/1
latch, never anything else. The four FCU knobs show only one direction.

## Positions (now `verified` with these values)

| Control | Values | Note |
|---|---|---|
| FLAPS lever | 0 1 2 3 4 | 4 = FULL |
| ENG MODE | 0 1 2 | rests at 1 at the gate |
| A/SKID & N/W STRG, ALT 100/1000 | 0 1 | |
| RUD TRIM, MAN V/S CTL | 0 1 2 | spring back to 1 (`rest: 1`, see below) |
| Transponder mode, TCAS traffic | 0 1 2 / 0 1 2 3 | confirmed |
| STROBE, NAV & LOGO, LAND L/R, NOSE, NO SMOKING, EMER EXIT LT | 0 1 2 | EMER EXIT LT is a 3-position switch, not a push button |
| BEACON, WING, RWY TURN OFF, SEAT BELTS | 0 1 | |
| IR 1/2/3 mode, X BLEED, ANN LT | 0 1 2 | |
| EFIS ADF/VOR 1 and 2, both sides | 0 1 2 | |
| WX radar SYS / MODE / MULTISCAN / PWS, ATC 1/2 | 0 1 2 / 0 1 2 3 / 0 1 / 0 1 / 0 1 | MODE gained 0 |
| ATT HDG, AIR DATA, EIS DMC, ECAM/ND XFR, AUDIO SWITCHING | 0 1 2 | |
| WIPER capt/FO, clock ET, clock UTC | 0 1 2 | |

## Buttons

- **+1/+1 push buttons (`press: edge`)**: AUTO BRK LO, APU START, RCRD GND CTL, RMP 1 VHF1 and
  VHF2, TERR ON ND, GPWS G/S, AUTOLAND, ATC MSG, PFD/ND XFER (captain). The first officer twin of
  each and the VHF1/2/3 keys of all three RMPs follow as the same part.
  Lamp checks only where the lamp followed the press: GND CTL, TERR ON ND (captain).
- **0/1 latches (owner: sync as `verified [0, 1]`)**: PRESS MODE SEL, HOT AIR, FUEL X FEED, FUEL
  MODE SEL, GPWS FLAP MODE, GPWS SYS, GPWS G/S MODE. One click toggled the value and its lamp;
  the lamp is now the check.
- So a button family cannot be assumed to be counters. Taken back to the review list for lack of
  evidence: the RMP keys other than VHF1/2/3, AUTO BRK MED/MAX, the A/THR disconnect buttons.

## Spring-loaded controls (owner decision)

RUD TRIM and MAN V/S CTL spring back. Every change is mirrored, so a hold lasts as long on both
sides. If the release never arrives (30 s, link lost), the receiving bridge writes the rest value
back, only for a switch it moved itself, with a red desync (`rest` in `SCHEMA.md`, protocol.md
section 5, CLAUDE.md safety rules).

## FCU knobs: one direction is invisible

SPD, HDG, ALT and V/S showed only -1, held as long as the mouse (1.3–1.6 s). The other direction is
set and reset inside one frame, so neither this tool nor the bridge can see it. No value is guessed.

For gate test 2 (owner decision):
- the four knobs stay unsynced: any visible move gives the amber "not synced yet" line;
- their managed lamps and dashed flags (`I_FCU_SPEED/HEADING/ALTITUDE_MANAGED`,
  `B_FCU_SPEED/HEADING/VERTICALSPEED_DASHED`) are compared as inputs: red after 2 s, never quiet,
  "SPD managed differs between the cockpits – push or pull the knob in the cockpit that is wrong."
  The pilot's fix stays local, because the knob is unsynced.

Proposed detector, after gate test 2: one read-only run (`positions --only FCU_SPD` etc.) with
the FMGS set up, so managed modes are available. The FCU steps now also record the dashed flags
and the `N_FCU_*` displays. If the hidden direction always changes one of them with no knob change,
the bridge can show that as its own line and, with owner approval, sync it.

## Not decided (stay for CRO-24)

- Not moved: TCAS mode (`S_XPDR_OPERATION`; the transponder knob was turned again, so the values
  from gate test 1 are kept), WX GAIN, ISFD LS, the second TCAS selector, LANDING BOTH, clock SET,
  FLAPS latch.
- Not in the run: see the review list (`unsynced.csv`, 200 controls, amber when moved).
