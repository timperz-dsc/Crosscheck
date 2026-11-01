# CRO-22 – Gate test 1: diagnosis

Two pilots flew the Fenix A320 (package 3.0.0.7613) at the gate, offline, both on bridge build 0.1.0 with the
same profile hash (`42be40…`). The host was pilot flying (PF) and the guest was pilot monitoring (PM).
This document finds the cause of every reported failure from the two bridge logs, the profile and the code. For
each failure it says whether the pilots were shown it. **No code was changed in this step.** The fix plan is at the
end.

Sources: the host log and the guest log (bridge JSONL, ~50,000 lines each, about 47 minutes), `profiles/fenix-a320/profile.yaml`,
`profiles/fenix-a320/draft-review.csv`, `bridge/Crosscheck.Bridge.Core/Sync/SyncEngine.cs`, `server/src/session/`.
The guest's clock ran about 9 s ahead of the host's; times below are as written in each log.

What worked: all FCU and EFIS knobs, ENG 1/2 MASTER, BRAKE FAN, ditching (cover and button), MAN V/S CTL, MAN START 1/2,
cargo smoke covers, ALT RPTG. These are absolute switches with correct values, or encoders.

---

## 1. The five hypotheses

| # | Hypothesis | Verdict | Evidence |
|---|---|---|---|
| 1 | Many push buttons are press counters; the second press gives 2 and the range rule rejects it | **Confirmed** for latching push buttons (ELEC PUMP, BUS TIE, SVCE INT OVRD, ADR/IR, AVIONICS COMPT, WR GCS, transponder digits). Momentary keys (MCDU, ECAM, ATC CLR) fail for another reason, see B | Guest log: `S_OH_HYD_YELLOW_ELEC_PUMP = 2: not one of the allowed values [0, 1]`, then 3, 4 … 28. The host sent values 1…28, two per click (press and release, about 100 ms apart) |
| 2 | Multi-position switches only allow their scan value (CRO-24) | **Confirmed** | `S_OH_SIGNS values: [0]`, `S_OH_EXT_LT_STROBE [0]`, `S_TCAS_RANGE [2]`; log: `S_OH_SIGNS = 1: not one of the allowed values [0]` (30 times) |
| 3 | Many vars are still on the review list and not synced at all | **Confirmed** | `S_FCU_AP1`, `S_FCU_EXPED`, `S_OH_ELEC_EXT_PWR`, `S_OH_ELEC_APU_START`, `S_FCU_EFIS1_CSTR`, `S_MIP_AUTOBRAKE_*`, `S_MIP_AUTOLAND_*`, `S_ENG_MODE`, `S_FC_FLAPS` … are in `draft-review.csv`, not in the profile. Neither log has a single command for them |
| 4 | Hold buttons are synced as a fixed pulse instead of mirroring press and release | **Confirmed** | FIRE TEST is `class: pulse, pulse_ms: 150`: the other sim gets 1, then 0 after 150 ms. SVCE INT OVRD is a press counter profiled as `verified [0, 1]` (see 1) |
| 5 | Repeated presses of the same key within the pulse time get merged | **Rejected** | Every MCDU letter key was sent **exactly once in the whole session** (`S_CDU1_KEY_S` 1, `S_CDU1_KEY_E` 1 …). The second S of ESSA was never sent, so nothing was merged. The real cause is B |

---

## 2. Causes, grouped

### A. Latching push buttons are press counters, but profiled as `verified [0, 1]`

Fenix adds 1 on press and 1 on release (odd = pressed), like MASTER CAUTION (`spike/VarTypes/TYPES.md`). Only the
first press (value 1) is inside `[0, 1]`. Every later value is refused by the safety guard on the receiving side.

| Item | Values in the log | Shown to the pilots? |
|---|---|---|
| ELEC PUMP (`S_OH_HYD_YELLOW_ELEC_PUMP`) – "on syncs, off doesn't" | 1…28 | Flicker only: 21 `refused` desyncs, on screen for a median of 0.11 s (cause G) |
| BUS TIE (`S_OH_ELEC_BUSTIE`) – fails "when switching back on" | 1…8 | Flicker, plus `correction_failed` |
| SVCE INT OVRD – "inverted" | 1…37. The other sim got the first value (1 = odd = held) and then refused every later one, so it stayed "held" | Flicker (35 times, median 0.45 s) |
| ADIRS ADR 1–3 and IR 1–3 push buttons | 1…4 | Flicker |
| AVIONICS COMPT LT, WR GCS, transponder digit keys `S_PED_ATC_n` | 1…14 / 1…8 / 1, 2 | Flicker |

**Fix:** profile these as `class: pulse` with `press: counter`. Counter detection and counter writing already exist
(`SyncEngine.cs:281`, `:670`). Buttons that are held (SVCE INT OVRD, FIRE TEST) need cause D's fix.

### B. Momentary keys are detected only once – **silent**

`SyncEngine.OnSimValue` (`SyncEngine.cs:281-283`) counts a non-counter pulse only when the var goes from 0 to
non-zero. After a click in the cockpit Fenix does not set the key back to 0, so every later click of the same key
is invisible to the bridge.

Proof from the host log: MCDU CLR was detected again **only right after** a write from the other side (1, then 0)
had reset it to 0: sent 19:04:21 → remote write 19:05:21 → sent again 19:05:28 → remote write 19:06:57 → sent 19:07:08.

Affected: all MCDU keys (ESSA → ESA and ENGM → NGM: E and S had already been used once), MCDU CLR, ECAM page keys
(each sent once; after cycling with ALL every key had been used), ECAM EMER CANC, transponder CLR, RUD TRIM RESET,
CALLS, CLOCK CHR.

No command is sent, so nothing is shown. The only indirect hint: `aircraft.mcdu1/2.display` observe mismatch (6 times each).

**Fix:** treat Fenix S_ push buttons as counters. On the sending side, count one press for each odd value passed in
an increase that is not our own write. This also catches two presses inside one frame. On the receiving side,
write c+1, c+2. First confirm the click values with one read-only scanner observation (owner approval, no writes).

### C. Controls not in the profile are never watched – **silent**

674 rows are still on the review list. `SyncEngine.PlanFor` subscribes only to profile vars, and `OnSimValue`
returns on unknown names (`SyncEngine.cs:248`).

Never synced: AP1, AP2, A/THR, LOC, APPR, EXPED, AUTO LAND, AUTO BRK LO/MED/MAX, EXT PWR (scan value 2), GND CTL,
EMER EXIT LT, APU START, EFIS CSTR/WPT/VOR.D/NDB/ARPT, EFIS ADF/VOR switches (`S_FCU_EFIS1_NAV1/2`), CHRONO, clock ET,
RMP VHF 1/2/3 select, INT/RAD (`S_ASP*_INTRAD`), N1 MODE push button, ENG MODE selector, flaps lever (`S_FC_FLAPS`),
RUD TRIM knob, A/SKID & N/W STRG (`S_FC_MIP_ANTI_SKID`).

- Tray table is `class: ignore` on purpose (cosmetic).
- VOICE is wrong: `S_ASP_VOICE` is `ignore` and only `S_ASP_VOICE_LATCH` is synced.
- "Worked once" for AP, AUTO BRK and AUTO LAND cannot have come from Crosscheck: no command exists. Most likely both
  pilots pressed. To be confirmed with the pilots.

Only partly visible: lamps such as `I_FCU_AP1`, `I_OH_ELEC_EXT_PWR_U` or `I_MIP_AUTOLAND_CAPT` raised
`observe_mismatch`, but without naming the control that caused it.

**Fix:** classify these controls with profile-probe (safe parts, owner-approved, FCU push buttons first). Until then
the bridge watches review-list S_/A_ vars read-only and shows "You moved X – not synced yet" (amber), so they are
never silent.

### D. Hold buttons are a fixed 150 ms pulse – **silent**

ENG 1 / APU / ENG 2 FIRE TEST were each sent once, as 1 → 0 after 150 ms. The ack is `ok` because the value was
stored, so nothing is shown. (The second press was not even detected, cause B.)

**Fix:** a new `press: hold` mode. Send a press event on an odd value and a release event on the next even value;
the receiver mirrors both, with no fixed timer. Safety: if the release never arrives, release after a cap (for
example 30 s) and raise a desync.

### E. Multi-position switches allow only their scan value (CRO-24)

Positions the pilots actually used in this test (valid pilot input, from the commands in both logs):

| Var | Profile | Seen |
|---|---|---|
| EXT LT STROBE, NAV & LOGO, LANDING L/R/BOTH | [0] | 0, 1, 2 |
| SIGNS (seat belts) / SIGNS_SMOKING | [0] | 0, 1 / 0, 1, 2 |
| WIPER CAPT / WIPER REPELLENT CAPT | [0] | 0, 1, 2 / 1, 2 |
| XPDR MODE / OPERATION / ATC | [0] | 0, 1, 2 / 0, 1, 2 / 0, 1 |
| TCAS RANGE | [2] | 0, 1, 2, 3 |
| WR MODE / SYS / MULTISCAN / PRED WS | [2] / [1] / [1] / [0] | 1, 2, 3 / 0, 1, 2 / 0, 1 / 0, 1 |
| AUDIO SWITCHING | [1] | 0, 1, 2 |
| PRESS MODE SEL, GPWS SYS, GPWS G/S MODE | [1] | 0, 1 |
| CARGO SMOKE DISCH AGENT 1/2 (`S_OH_CARGO_DISC_*_OLD_LAYOUT`, three positions) | [0, 1] | 0, 1, 2 – only "downwards" synced |
| WINDOW BLINDS, WINDOW LEVER | [0] | 0, 1 |
| COCKPIT DOOR | [1] | 0 |
| WINDOW CAPT LATCH | [0, 1] | 0, 0.1, 1 (0.1 is an in-between animation value) |

Not reached in this test because they are not in the profile (cause C): ENG MODE, CLOCK ET and the ECAM switching
panel (ATT HDG, AIR DATA, EIS DMC). The vars for the ECAM switching panel were not found under the expected names
and still have to be located in the scan.

**Interim fix:** add the positions seen above to the profile (`overrides.yaml` → profile-gen), with the owner
confirming the full position count (TCAS, for example, may have more). Treat WINDOW LATCH 0.1 as a transient.

**Real fix: CRO-24 learn mode.** A value read from a pilot's real cockpit (a local change that is not our own write)
is a valid position. The bridge adds it to a learned-values list for that var and sends it; the receiver accepts it
because it was learned from real pilot input. Nothing is guessed or stepped through. Learned values are logged,
shown for review and can be written back into the profile. Open design question for CRO-24: how the receiving side
trusts a value learned on the other sim (for example, the server marks commands that come from a real pilot change).

### F. Analog levers and knobs are not 0…1 (new cause)

`range: [0, 1]` is wrong for:

| Var | Values seen | Effect |
|---|---|---|
| LDG ELEV (`A_OH_PNEUMATIC_LDG_ELEV`) | −3 … 6.5 in 0.25 steps | Only values inside 0…1 got through: "syncs, but with a long delay" (48 refused, 3 failed corrections) |
| Speedbrake (`A_FC_SPEEDBRAKE`) | 1.0 … 2.05 | Refused |
| WR TILT / WR GAIN | 3 … 10 / −5 … −1 | Refused |
| Throttles (`A_FC_THROTTLE_*_INPUT` / `_GATE`) | 2 … 3.6 / 3, 6, 7, 8 | Refused |

The server also sent a correction for `A_FC_THROTTLE_LEFT_GATE = 3` to the PM. The safety guard refused it, which is
correct, but throttle positions are hardware axes and must never be corrected.

**Fix:** ranges from the values seen, confirmed by the owner. Throttles become `ignore` until the stream (CRO-17).
The flaps lever needs classifying (C).

### G. The next command wipes an open desync, even when it is refused too – **effectively silent**

`server/src/session/session.ts:288-289` calls `clearDesync(var, 'pilot_command')` for every new command. On counter
buttons and encoders the next value arrives about 100 ms later, so a "refused" desync was on screen for 0.1–0.5 s
(median) and then gone ("a pilot acted on it"). 355 of the 393 `refused` desyncs ended this way.

**Fix:** a new pilot command no longer clears an input desync. It clears only when that var's own command is acked
`ok`, or when a snapshot shows the values match.

### H. The encoder check is too strict (false "refused" on the FCU knobs)

For `delta` vars, the first check waits until the counter equals the exact target within 250 ms
(`SyncEngine.cs:715`). When the pilot turns fast, the counter has already moved on and the check fails, although the
display matches. E_FCU_SPEED: 74 acks with `stored: false` next to 405 good ones, and 57 `refused` desyncs that
disappeared within a fraction of a second (cause G). That is why the pilots saw no desync on the knobs.

**Fix:** for `delta`, step 1 passes when the counter reached or passed the target in the turn direction. The proof is
the `display` var after settle, which the snapshot already compares.

### I. A pulse ack ignores the effect var – **silent** (FD)

The host pressed FD 11 times. The guest wrote the counter 1…22 and acked all 11 presses `ok`, but `I_FCU_EFIS1_FD`
stayed at 1 from the second press on (`applied … effect: 1, ok: true`). `VerifyAndAck` records the effect var but
only checks it for GraphQL and MCDU text (`SyncEngine.cs:732-735`). The pilots got no warning.

Why Fenix ignored those presses cannot be told from the logs. It needs one targeted test (owner-approved, FD only,
+1/+1 steps from the current value).

**Fix:** when `verify.lvar` exists, a pulse that does not change the effect (or its parity, `expect: parity`) is
acked `ok: false`, which raises a desync.

### J. Other items

| Item | Cause | Shown? | Fix |
|---|---|---|---|
| PARK BRK with both pilots on hardware | Each hardware lever keeps writing its own position: host 32 commands, guest 8, 6 `overridden` notices | Only in the Recent list | Rule: only the PF binds the parking brake. Amber "hardware fighting" line after 3 overrides in 30 s |
| RMP standby/CRS sometimes different | Inner knob: cause H. Outer knob: **neither side ever sent `E_PED_RMP1_OUTER`**, but the displays differed (`N_PED_RMP1_STDBY` differed in 18 snapshot diffs) | As observe noise | Read-only check whether the cockpit outer knob uses this var, then profile |
| RMP 2/3 off on the guest side (`N_PED_RMP2_ACTIVE` = −1) | RMP 2/3 POWER never synced (only RMP 1 POWER was sent) | As observe noise | Check the RMP 2/3 POWER profile entries |
| LDG ELEV long delay | F | Flicker | F |
| Standby instruments | Not in the profile (expected) | – | Later |
| 354 `missing_value` desyncs at the end | The host's sim disconnected at the end of the session. `OnSnapshotRequest` (`SyncEngine.cs:824`) still answered with empty values; `SendHashes` checks the sim state, this path does not | Noise, stayed open | Do not answer a snapshot while the sim is not connected; the server marks the group "not comparable" |

---

## 3. Every silent case and the code path that dropped it

The rule is that no desync may be silent. These were:

| # | Case | Code path |
|---|---|---|
| S1 | Controls not in the profile (C) | `SyncEngine.PlanFor` subscribes only to profile vars; `OnSimValue`: `if (!_vars.TryGetValue(name, out var spec)) return;` (`SyncEngine.cs:248`) |
| S2 | Momentary keys after the first press (B) | `SyncEngine.cs:281-283`: `prev == 0 && value != 0` |
| S3 | Hold buttons released after 150 ms (D) | `SyncEngine.Write`, pulse branch (`:668-689`); ack `ok` = value stored |
| S4 | Pulse with no effect (FD, I) | `SyncEngine.VerifyAndAck` (`:720-748`): `verify.lvar` is recorded, never compared |
| S5 | Refused desync wiped by the next click (G) | `server/src/session/session.ts:288-289`: `clearDesync(body.var, 'pilot_command')` |
| S6 | VOICE key | Profile: `S_ASP_VOICE` is `class: ignore` |
| S7 | RMP outer knob | No command was ever produced (detection or var name, to be checked) |

Not silent, but useless in practice: so many indicators differed that the real failures drowned (section 4).

---

## 4. Desync panel noise

### What fired (guest log; both sides received the same server messages)

| Reason | Desync messages | Kind |
|---|---|---|
| `observe_mismatch` | 936 (568 vars); 408 of them before the end-of-session burst | Indicators and derived values |
| `refused` | 393 | Pilot input that didn't arrive (A, E, F, H) |
| `missing_value` | 355 (354 still open at the end; overhead 147, pedestal 156, MIP 21, cabin 12, EFIS 10, flight controls 8, ECAM 1) | End-of-session artefact (J) |
| `correction_failed` | 54 | Pilot input (A, E, F) |
| `correction_loop` | 6 | Encoders (H) |
| `drift` (not a desync) | 1,289 messages for one var, `aircraft.fuel.left.amount.kg` (up to 2,250 kg) | Owned value: correctly never written, but far too many messages |

#### Pilot input that didn't arrive (counts per var)

| Var | Desyncs |
|---|---|
| `E_FCU_SPEED` | refused 57 |
| `A_OH_PNEUMATIC_LDG_ELEV` | refused 48, correction_failed 3 |
| `S_OH_SVCE_INT_OVRD` | refused 42, correction_failed 7 |
| `S_OH_HYD_YELLOW_ELEC_PUMP` | refused 26, correction_failed 5 |
| `S_OH_SIGNS` | refused 29, correction_failed 1 |
| `S_OH_SIGNS_SMOKING` | refused 16, correction_failed 1 |
| `E_FCU_HEADING` | refused 15 |
| `E_PED_RMP1_INNER` | refused 11, correction_loop 2 |
| `S_OH_EXT_LT_STROBE` | refused 10, correction_failed 1 |
| `S_OH_ELEC_BUSTIE` | refused 6, correction_failed 4 |
| `S_OH_LIGHTING_AVIONICS_COMPT` | refused 8, correction_failed 2 |
| `E_FCU_ALTITUDE` | refused 8 |
| `E_FCU_EFIS1_BARO` | refused 7, correction_loop 1 |
| `S_WR_GCS` | refused 7, correction_failed 1 |
| `A_FC_THROTTLE_LEFT_GATE` | refused 3, correction_failed 2 |
| `E_FCU_EFIS2_BARO` | refused 4, correction_loop 1 |
| `S_TCAS_RANGE` | refused 4, correction_failed 1 |
| `A_FC_THROTTLE_LEFT_INPUT` | refused 2, correction_failed 2 |
| `A_WR_TILT` | refused 3, correction_failed 1 |
| `S_OH_CARGO_DISC_2_OLD_LAYOUT` | refused 4 |
| `S_OH_NAV_IR1/2/3_SWITCH` | refused 3, correction_failed 1 (each) |
| `S_OH_NAV_ADR2` | refused 3, correction_failed 1 |
| `S_OH_NAV_ADR1`, `S_OH_NAV_ADR3` | refused 2, correction_failed 1 (each) |
| `A_FC_THROTTLE_RIGHT_GATE`, `A_FC_THROTTLE_RIGHT_INPUT` | refused 2, correction_failed 1 (each) |
| `S_WR_MODE`, `S_WR_SYS`, `A_WR_GAIN` | refused 2, correction_failed 1 (each) |
| `A_FC_SPEEDBRAKE` | refused 3 |
| `S_PED_ATC_0/4/5/6/7` | refused 2, correction_failed 1 (each) |
| `S_MISC_WIPER_REPELLENT_CAPT` | refused 2, correction_failed 1 |
| `S_MISC_WIPER_CAPT`, `S_WINDOW_CAPT_LEVER`, `S_COCKPIT_DOOR`, `S_WINDOW_BLINDS_REAR_CAPT`, `S_WINDOW_BLINDS_FO` | refused 1, correction_failed 1 (each) |
| `S_OH_EXT_LT_NAV_LOGO`, `S_OH_EXT_LT_LANDING_L/R/BOTH`, `S_XPDR_OPERATION`, `S_XPDR_ATC`, `S_XPDR_MODE`, `S_WINDOW_CAPT_LATCH`, `S_MIP_MASTER_CAUTION_CAPT`, `S_AUDIO_SWITCHING`, `A_OH_LIGHTING_READING_FO` | refused 2 (each) |
| `S_STAIRS_BLOCKER` | correction_failed 1 |
| `E_PED_RMP1_OUTER`, `E_PED_RMP3_INNER` | correction_loop 1 (each) |
| `S_OH_GPWS_GS_MODE`, `S_OH_GPWS_SYS`, `S_FC_FO_INST_DISCONNECT`, `A_PED_LIGHTING_PEDESTAL`, `A_MIP_LIGHTING_FLOOD_MAIN`, `S_WR_PRED_WS`, `S_WR_MULTISCAN`, `S_FC_RUDDER_TRIM_RESET`, `S_OH_CARGO_DISC_1_OLD_LAYOUT`, `S_OH_PNEUMATIC_PRESS_MODE`, `S_MIP_MASTER_WARNING_CAPT`, `E_CB_49VU_G01`, `E_CB_49VU_G02`, `S_WINDOW_BLINDS_CAPT`, `S_WINDOW_BLINDS_REAR_FO` | refused 1 (each) |

#### Derived values (before the end-of-session burst)

| Var | Desyncs | Largest difference |
|---|---|---|
| `N_FC_RUDDER` | 12 | 94 |
| `N_FC_CAPT_TILLER` | 11 | 17 |
| `B_FC_RUDDER_TRIM_DASHED` | 5 | 1 |
| `B_ELEC_BUS_POWER_*` (AC 1, AC 2, AC ESS, AC ESS SHED, AC STAT INV, DC 1, DC 2, DC ESS SHED, DC SERVICE) | 4 each | 1 |
| `B_FC_RUDDER_TRIM` | 4 | 1 |
| `B_EXT_HYD_GEAR_DOOR_PRESSURE`, `B_FC_ELEVATOR_TRIM_MOTOR_POWER`, `B_PED_XPDR` | 3 each | 1 |
| `N_CDU1/2_BRIGHTNESS` | 3 each | 0.25 |
| `N_DCDU1/2_BRIGHTNESS` | 2 each | 0.25 |
| `N_OH_LIGHTING_OVD`, `N_PED_LIGHTING_PEDESTAL` | 3 each | 0.6 |
| `N_FC_ELEVATOR_TRIM` | 3 | 0.008 |
| `N_ELEC_VOLT_BAT_1` / `_2` | 1 / 2 | 0.1 V |
| `N_FC_BRAKE_RIGHT` | 2 | 0.8 |
| `N_FC_SIDESTICK_CAPT_BANK` / `_PITCH` | 2 each | 411 / 992 |
| `N_HYD_PRESSURE_BRAKE_LEFT/RIGHT` | 1 each | 0.75 |
| `N_FUEL_PRESEL`, `N_FUEL_TOTAL_QTY`, `N_FUEL_WING_TANK_L/R` | 1 each | 7.5 / 4.5 / 2.25 |
| `N_FC_RUDDER_TRIM_DECIMAL` | 1 | 7 |
| `N_MIP_CLOCK_CHRONO`, `N_MIP_CLOCK_ELAPSED` | 1 each | 2 / 1 |
| `N_PED_XPDR_CHAR_DISPLAYED` | 1 | 3 |
| `N_WINDOW_CAPT` | 1 | 50 |

#### Others: displays of pilot input, and lamps (before the end-of-session burst)

Displays (the result of a pilot input, so they belong with the input): `aircraft.mcdu1.display` 6,
`aircraft.mcdu2.display` 6, `N_PED_RMP3_STDBY` 4, `N_PED_RMP2_ACTIVE/STDBY` 3, `N_PED_RMP3_ACTIVE` 3,
`B_PED_RMP2/3_POWER` 3, `B_PED_RMP3_DATA_DISPLAYED_STDBY` 3, `N_FCU_EFIS1/2_BARO_HPA/INCH` 2–3, `N_FCU_LIGHTING` 3,
`N_FCU_SPEED` 2, `N_PED_RMP1_ACTIVE` 2, `N_PED_RMP1_STDBY` 1, `B_FCU_SPEED/HEADING/VERTICALSPEED_DASHED` 1–2,
`B_PED_RMP1/3_DATA_DISPLAYED_ACT` 1.

Lamps (`I_*`, 109 vars): mostly effects of an input that didn't arrive, for example `I_ECAM_CLR_LEFT/RIGHT` 9,
`I_MIP_MASTER_CAUTION_*` 7, `I_CDU*_RDY/FM/FAIL` 2–5, `I_OH_FUEL_*` 3, `I_OH_FLT_CTL_*` 1–4, `I_OH_ELEC_EXT_PWR_U` 3,
`I_OH_ELEC_APU_START_L` 2, `I_OH_ELEC_BUSTIE_L` 2, `I_OH_HYD_YELLOW_ELEC_PUMP_L` 3, `I_OH_SVCE_INT_OVRD` 3,
`I_FCU_AP1/AP2/ATHR` 1–2, `I_MIP_AUTOLAND_*` 1, `I_OH_FIRE_*` 1, `I_OH_NAV_IR*_ALIGN` 1, `I_PED_RMP*` 1–3.

### Proposal per derived value

| Var group | Proposal | Tolerance |
|---|---|---|
| Battery volts `N_ELEC_VOLT_BAT_*` | observe with tolerance | ±0.5 V (a real fault differs by volts) |
| Hydraulic and brake pressures `N_HYD_PRESSURE_*` | observe with tolerance; brake pressure quiet only | ±10 % of full scale |
| Temperatures (cabin/duct, when added) | observe with tolerance | ±2 °C |
| Fuel gauges `N_FUEL_*` | ignore (the owned GraphQL fuel drift already covers fuel) | – |
| Brightness `N_*_BRIGHTNESS`, `N_*_LIGHTING*` | ignore (they follow the analog knobs, which are synced themselves) | – |
| Flight-control positions `N_FC_RUDDER/TILLER/SIDESTICK/BRAKE/ELEVATOR_TRIM`, `N_FC_RUDDER_TRIM_DECIMAL` | ignore until the stream (CRO-17) | – |
| Clocks `N_MIP_CLOCK_CHRONO/ELAPSED` | ignore (differ by start moment) | – |
| `N_WINDOW_CAPT` (window position) | ignore (animation) | – |
| FCU/RMP/MCDU displays | not indicators: they are the `display` of an input → input tier | exact |
| `B_ELEC_BUS_POWER_*`, `B_*` flags, `I_*` lamps | observe, indicator tier, must differ for ≥ 10 s | exact |

Mechanism: a new profile field `tolerance` for observe entries. Snapshot values carry it, and the server's
`diffValues` (`server/src/session/corrections.ts`) uses it. Vars with a tolerance are left out of the exact group hash
and compared in the periodic values request instead, so the hash doesn't flap.

### How the panel separates real failures from noise

The server sends a `tier` with each desync (protocol and schema change):

1. **Red – "Your input didn't arrive."** `refused`, `no_ack`, `correction_failed`, `log_conflict`, a pulse without
   effect, and input vars (S_/A_/E_ and their displays) in a snapshot diff. Always at the top, never collapsed,
   sticky: cleared only by an `ok` ack for that var or a matching snapshot (fix G).
2. **Amber – one line each.** "Not synced yet" for a control outside the profile that a pilot touched (S1),
   "Hardware fighting" for repeated overrides (parking brake), and owned drift ("Fuel differs by X kg – load the same
   fuel in the EFB").
3. **Quiet – "Indicators differ (n)".** Observe vars over their tolerance for at least 10 s, collapsed into one line
   with a count; details on click. Lamps in a group that has an open red input desync are listed under that red item
   as "likely effects", not on their own. Never red, never in the headline count.

A pilot's failed input can never land in the quiet tier, so it can never drown in noise.

---

## 5. Fix plan, in priority order

1. **Nothing silent (P0).** G: the server keeps input desyncs until an `ok` ack or a match. I: check the effect var on
   pulses. S1: an amber "not synced yet" watcher. The three panel tiers (section 4). Drift: send it only when the
   difference changed by more than a step (per dataref, for example 50 kg for fuel) or at most every 10 s (`sendDrift`
   in `server/src/session/session.ts`), shown as one amber line, never as a desync.
2. **Profile and classes (P0).** A: counter push buttons → `pulse` + `press: counter`. E interim: the positions seen in
   this test. F: ranges; throttles → `ignore`. VOICE class. J: no snapshot answer while the sim is disconnected.
   Mostly `overrides.yaml` → profile-gen.
3. **Press detection (P1).** B: count presses for all S_ push buttons. First a read-only observation of the click
   values (owner approval).
4. **Hold mode (P1).** D: `press: hold`, mirroring press and release, with a safety cap.
5. **Encoders (P1).** H: verify by turn direction and the display var.
6. **Review-list controls (P1).** C: classify with profile-probe (safe parts, owner-approved), FCU push buttons first.
7. **CRO-24 learn mode (P1).** The real fix for E: a position read from a pilot's real cockpit is a valid value. The
   interim profile values stay until learn mode has passed a gate test.
8. **Tolerances (P2).** The `tolerance` field and the ignore list from section 4.
9. **Targeted tests (P2).** FD stuck at 1 (I), which var the RMP outer knob uses (J), parking brake procedure
   (only the PF binds it).

### Verification for the fixes

- Replay tests: feed the recorded value sequences from this test into the fake-bridge tests (counter values 1…28,
  fast MCDU typing such as ESSA/ENGM, a refused value followed by more clicks). Assert that every press is sent and that
  a desync stays visible.
- Server, bridge and UI unit tests for the tiers, drift rate and tolerances.
- Gate test 2 with the same checklist.

---

## 6. Addendum (CRO-27): corrections after a second look at the logs

- **Ranges (F).** The logs go a little further than the table in section 2 F: LDG ELEV −3 … **7**, speedbrake 1 … **3**,
  WR GAIN −5 … 0 (0 in the scan and snapshots). The profile uses these ranges (owner confirmed).
- **Analog GraphQL check.** It was wrong for every analog knob, not only out of range. LDG ELEV read about 200–250 in
  `system.analog` for knob values 0 … 1 (not value × 1024), so all 30 applied LDG ELEV writes acked `ok: false`, even
  inside [0, 1]. Lighting, volume and temperature knobs failed the same way. The check was removed from all 102 analog
  entries; they are confirmed by read-back of their own L: var. Calibrating the scale is CRO-28.
- **VOICE (S6).** Not only a class question: when the host pressed VOICE (19:10:19) `I_ASP_VOICE` went to 1 while
  `S_ASP_VOICE_LATCH` stayed 0. A click on VOICE moves only the key; a click on a REC key moves its `_LATCH`. The VOICE
  keys of ASP 1–3 are now pulses checked against their lamp.
- **RMP 2/3 POWER (J).** Not a profile problem. All three POWER switches were 1 on both sims for the whole session and
  nobody moved RMP 2 or 3; only RMP 1 was toggled (19:11), which is why only it was sent. The difference was the output
  `B_PED_RMP2/3_POWER`: on the guest it came on only after the AC power transfer at 18:57:02, about four minutes after
  the host. The entries are unchanged.
- **FO takeover button.** `S_FC_FO_INST_DISCONNECT` went 0 → 1 → 2 → 0 within 1.5 s; the 2 was refused. It is a held
  button and goes with FIRE TEST and SVCE INT OVRD into the hold step (cause D).
