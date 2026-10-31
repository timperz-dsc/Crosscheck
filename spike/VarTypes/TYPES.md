# Fenix A320 control types: write and read-back

Measured on 2026-10-31 with MSFS 2024 and the Fenix A320 powered at the gate (batteries and external power). Each control had 10 iterations, every write is one sample, and all values were restored afterwards. Raw samples are in `results.csv` (gitignored); the full log is in `test_run.txt`.

All writes go through SimConnect `SetDataOnSimObject` on the L: var (`"number"`, FLOAT64). Times are ms from the write:
- **L: read-back:** median of the same L: var showing the written value.
- **Effect:** another L: var showing the aircraft reacted.
- **GraphQL:** Fenix's own dataref seen by HTTP polling. The subscription is not usable as a read-back source for these, see GRAPHQL.md.

| type | example var | write method | read-back source | L: read-back median (min/p95/max) | effect / GraphQL median | samples | failures | notes |
|---|---|---|---|---|---|---|---|---|
| multi-position rotary | `L:S_OH_PNEUMATIC_PACK_FLOW` (3 pos), `L:S_FCU_EFIS1_ND_MODE` (5), `L:S_FCU_EFIS1_ND_ZOOM` (6) | absolute position 0..n−1 | same L: var + `system.switches.*` | 14.0 / 14.0 / 14.6 (3.1 / 25 / 49) | GraphQL 69 / 70 / 69 | 30 / 50 / 60 | 0 | every position stepped through. GraphQL uses the same numbers for these three, but the dome light is reversed (L: 0/1/2 = GraphQL 2/1/0) |
| guarded cover | `L:S_OH_HYD_RAT_MAN_ON_Cover`, `L:S_OH_ELEC_IDG1_Cover` | 1 = open, 0 = closed | same L: var only | 16.7 / 15.2 (3.9 / 25 / 26) | no GraphQL mirror for covers | 20 / 20 | 0 | the guarded switch was watched on every write and never moved |
| FCU encoder | `L:E_FCU_ALTITUDE`, `L:E_FCU_HEADING`, `L:E_FCU_SPEED` | **relative counter**: write current+1 for one click clockwise, −1 for anticlockwise. Fenix applies only the change, never the absolute value | `L:N_FCU_ALTITUDE/HEADING/SPEED` + `system.numerical.N_FCU_*` | 19.3 / 10.9 / 16.8 (3.5 / 25 / 38) | effect 110 / 103 / 117; GraphQL 72 / 70 / 75 | 20 / 20 / 20 | 0 | one click = 1000 ft (scale switch on 1000; 100 ft is the minimum), 1°, 1 kt. A test across the 100 kt clamp confirmed delta semantics. `AUTOPILOT ALTITUDE LOCK VAR` stays at 0: Fenix doesn't use the default AP simvars |
| analog knob | `L:A_OH_PNEUMATIC_COCKPIT_TEMP`, `L:A_DISPLAY_BRIGHTNESS_CO` | absolute **0.0–1.0** (values above 1 are clamped) | same L: var + `system.analog.*` (= value × 1024) | 21.9 / 13.5 (6.3 / 29 / 29) | GraphQL 76 / 69 | 10 / 10 | 0 | tested 0, 0.05, 0.1, 0.25, 0.33, 0.5, 0.66, 0.75, 0.9 and 1. The actual brightness `N_DISPLAY_BRIGHTNESS_CO` ramps after the knob over about 1 s |
| momentary pushbutton (toggle function) | `L:S_ECAM_ENGINE`, `L:S_ECAM_HYD` | **press = 1, then 0 after 150 ms**. Fires on the rising edge, and Fenix never resets the var itself | `L:I_ECAM_ENGINE/HYD` (lit = SD page selected) + `system.indicators.*` | 17.9 / 17.0 (4.4 / 27 / 28) | effect 116 / 113; GraphQL 76 / 74 | 20 / 20 | 0 | a second press deselects the page. Same pattern as the CDU keys (spike/McduReader) |
| momentary pushbutton (press counter) | `L:S_MIP_MASTER_CAUTION_CAPT` | the cockpit model adds 1 on press and 1 on release, so **odd = held**. Write +1, then +1 again (writing 1 then 0 also works) | same L: var + `system.switches.*` (shows the parity) | 10.2 (4.3 / 22 / 22) | GraphQL 64 | 10 | 1 | it stood at 24 before the test (12 earlier presses). With no caution active the press has no visible effect, so only the switch state is verified. The 1 failure was on the first press: the HTTP poll missed the 700 ms hold. The L: read-back was fine |
| indicator (read-only) | all `L:I_*` from both profiles | — | L: var + `system.indicators.*` | — | — | 495 vars | 0 | 495 of 495 read. 37 lit, among them 3 green gear lights, GPU AVAIL, EXT PWR, the 6 fuel pump lights, GEN 1/2 FAULT, PACK 1/2, FCU SPD/HDG-VS mode and the QNH dots. All 37 match GraphQL exactly (`indicators.txt`) |

## Conclusions

- **Every control type writes and reads back through its own L: var.** All 330 samples were read back, with a median of 10–22 ms and a maximum of 49 ms.
- **The L: var echo doesn't prove the aircraft reacted.** Fenix accepts any value (for example out-of-range analog values) and stores it. The real confirmation comes from:
  - the effect var (`N_FCU_*`, `I_*`) at about 105–120 ms,
  - Fenix's GraphQL state at about 70 ms.
  Covers have neither of these.
- **Write semantics differ by type, and a sync engine needs a `class:` per entry:**
  - `rotary` / `cover` / `analog`: set the absolute value.
  - `encoder`: send deltas, never absolute values. The two simulators' counters will differ, and only the change counts.
  - `button-edge`: 1 then 0. Never sync the held value directly, or the other side gets a stuck press or misses one.
  - `button-counter`: sync the parity, or forward +1 events.
  - `indicator`: read-only, never written.
