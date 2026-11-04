# First two-PC flights (0.6.0) – diagnosis

Date: 2026-11-04. Bridge 0.6.0, profile `sha256:8ab36a58…`, Fenix A320 3.0.0.7613.
Logs: `gatetest-logs/bridge-20261104-170800-24860-6513ea.jsonl` (host),
`gatetest-logs/bridge-20261104-170826-33824-d64bac.jsonl` (guest). Both logs cover both flights.
Times are host-log UTC unless noted. The host clock runs about 11 s behind the server, the guest's about 0.4 s.

This document only diagnoses. The one code fix done so far is the write-rejection fix (part A, below).

## Timeline

| Time | What |
|---|---|
| 17:08:23 | Flight 1: host creates the session (PF). Guest joins 17:08:48 (follower). |
| 17:16:43–17:17:33 | Four quick control handovers on the ground. |
| 17:17:33–17:48:02 | **Guest is PF**, host follows. Guest leaves at 17:48:02 with the host in the air (`pf_lost_hold`, freezes released on leave). |
| 17:52:22 | Flight 2: new session, host PF. Guest joins 17:53:41 (after 9 `join_rejected`, wrong code). |
| 18:02:48–18:02:52 | Quick handovers on the ground. |
| 18:36:39–18:37:25 | Guest PF briefly, then host PF until the end (18:40:54). |

## Key finding: many symptoms come from the write-rejection bug

Each bridge blocked 8 vars early in the run, and they stayed blocked across both flights (same bridge process):

- Guest (17:08:48–17:09:06): A_ASP_INT_VOLUME, S_ASP_INT_REC_LATCH, **A_FC_SPEEDBRAKE**, S_OH_INT_LT_EMER,
  S_OH_EXT_LT_NAV_LOGO, S_OH_OXYGEN_CREW_OXYGEN, **S_OH_RCRD_GND_CTL**, A_OH_PNEUMATIC_FWD_TEMP.
- Host (17:12:49–17:17:02): S_ARMREST_LEFT_FO, **E_FCU_EFIS1_BARO**, S_OH_FUEL_LEFT_2 / CENTER_2 / RIGHT_2,
  A_PED_LIGHTING_PEDESTAL, **S_CDU1_KEY_FPLN**, **S_CDU1_KEY_LSK2L**.

Cause: the bridge shares 8 write slots. On each slot's first use it cleared a definition the sim had never seen.
SimConnect answers that with UNRECOGNIZED_ID, and the bridge read it as "this var cannot be written". The writes
themselves landed (e.g. host S_ARMREST_LEFT_FO `applied ok` 120 ms after its rejection). 8 slots = 8 exceptions
per side. Fixed in `bridge/Crosscheck.Bridge.Core/Sim/SimDefinitions.cs` (see `docs/protocol.md` section 5).

All 175 `cannot_write` desyncs, all `press_lost` / `stuck_pressed` / `press_mismatch`, 16 refused "Release it"
clicks and several observe/display desyncs come from this. A_FC_SPEEDBRAKE differed in 1680 of 1683 snapshots.

Side note: A_FC_SPEEDBRAKE was sent as 1.0058 and 1.0039. That is inside its profile range [0, 3] (1 = RET):
analog noise around RET, not an out-of-range write.

---

## 1. Follower N1 / EPR, FD bar, roll spoilers

**Reported:** follower N1 about 80 % while the PF shows about 60 % in OP DES; follower FD pitch bar far down;
roll spoilers move on the follower's WHEEL page.

**Evidence**
- Thrust levers are **not written** on the follower. They are `class: stream` without a `range`
  (`profiles/fenix-a320/profile.yaml:10436-10447`), and `SimSafetyGuard` only allows a stream var with a confirmed
  range (`SimSafetyGuard.cs:25-27`, `PhysicsSync.cs:440-448`). Lever values are streamed but not logged.
- The follower writes every frame (`FollowerSafety.cs:35-57`, `PhysicsLink.cs:75-94`): position, pitch, bank,
  heading, VELOCITY BODY Y/Z, VELOCITY WORLD X/Z, VERTICAL SPEED, G FORCE, ELEVATOR / AILERON / RUDDER POSITION.
  Not written: spoilers, flaps, levers, VELOCITY BODY X, VELOCITY WORLD Y, rotation velocities.
- No N1, EPR, FD bar or FMA var is in the logs, so the reported numbers cannot be checked from them.
- What did differ between the cockpits:
  - A_FC_SPEEDBRAKE in 1680/1683 snapshots (guest write blocked). At 17:46:28–17:47:56 and 18:33:05–18:35:25 one
    side had the lever at 3 (full) and the other at 0.
  - I_FCU_HEADING_MANAGED: host 0 / guest 1 from 17:35:43 to 17:46:33 (lateral mode differed for 11 minutes).
  - I_FCU_ATHR differed at 17:27:17, 18:32:56–18:33:58, 18:37:48 and 18:39:58–18:40:48.
  - E_FCU_EFIS1_BARO: 1023 vs 1032 in 887 of 890 flight-1 snapshots (host write blocked).

**Most likely cause.** The follower's own Fenix systems keep running: FADEC and A/THR on the follower's own lever
position, FMGC on its own FCU and MCDU state, the flight-control laws on a frozen aircraft that is fed G FORCE,
vertical speed and velocities but does not respond to their output. A/THR on the follower sees an aircraft that
does not slow down or speed up as it expects and keeps adding thrust; the FD computes against a different state.
The spoilers on the WHEEL page are partly the speedbrake lever (blocked write, so a different lever position) and
possibly the follower's laws commanding roll spoilers against the written attitude.

**Options (best first)**
1. Re-fly with the part A fix. Speedbrake, BARO and FCU mode differences should be gone, which removes part of it.
2. Measure the thrust lever range (item 7), let Sebastian confirm it, then write the levers on the follower.
   This makes the follower's A/THR / FADEC see the same lever. A/THR may still fight a frozen aircraft.
3. Test whether writing G FORCE and VERTICAL SPEED feeds the follower's laws. If yes, stop writing them (they were
   added for instruments) and compare.
4. CRO-43: take computed values from the PF instead of trusting the follower's own (N1, EPR, FMA, FD) and show the
   PF's values to the PM. Bigger change; only if 1–3 are not enough.

**Sim test needed:** yes, for 2 and 3 (and to confirm 1).

## 2. Fuel burn, gross weight, brake temperatures

**Reported:** GW 61300 vs 61100; brake temps 75/65 vs 10/10.

**Evidence**
- Only one fuel value is compared: `aircraft.fuel.left.amount.kg` (356 `drift` messages). GW and brake
  temperatures are not in the profile, so not in the logs.
- `delta` = PM − PF (negative: the follower has less fuel):

  | Flight | When | delta (kg) |
  |---|---|---|
  | 1 | 17:17–17:31 | −0.5 to +3 (tracking) |
  | 1 | 17:33:53 | −40 |
  | 1 | 17:39:15 | −175 |
  | 1 | 17:44:31–17:48:02 | about −230, flat |
  | 2 | 18:06–18:21 | within ±0.5 |
  | 2 | 18:22:28 | −24 |
  | 2 | 18:30:44 | −93 |
  | 2 | 18:35:34 | −143 |

  (One spike of +547 at 17:24:21 is a one-sample skew; both tanks jumped to 2000 kg at 17:24:25.)
- In both flights the gap opens when the PF reduces thrust and stops growing about 10–12 minutes later. Between
  17:32:23 and 17:33:53 the PF burned about 25 kg from the left tank, the follower about 63 kg.

**Most likely cause.** Follows from item 1: the follower's engines run at higher N1, so they burn more fuel, so
GW drifts. Brake temperatures: the follower's own brake model runs on its own (frozen, placed) aircraft and its own
brake inputs; the PF's brakes heat up from real braking. Not a sync bug as such.

**Proposed fix**
- Add GW and brake temps (GraphQL, if readable) as `owned` / observe values so the drift is visible, not guessed.
- Fuel sync from the PF: write the follower's tank quantities from the PF's values while following. That changes
  `owned` (never written) into "written on the follower only", which needs Sebastian's decision and a rule in
  CLAUDE.md (allowed range: 0 to tank capacity, follower only, never on the PF). Fixing item 1 first may make it
  unnecessary.

**Sim test needed:** yes (is tank quantity writable without side effects in Fenix?).

## 3. CRO-41: follower ground speed on the ground

**Reported:** on the ground the follower's GS jumps to 25 at once and is only right below about 25 kt; only on the
side not in control.

**Evidence**
- Playout is healthy. `stream_stats` while following:

  | Log / window | placed | interpolated | extrapolated | holding | max gap |
  |---|---|---|---|---|---|
  | host 17:17:33–17:48:02 | 74828 | 73158 | 1658 | 8 | 725 ms |
  | guest 17:53:42–18:36:39 | 87944 | 86680 | 1247 | 9 | 911 ms |
  | guest 17:08:48–17:16:55 | 16871 | 16631 | 238 | 2 | 750 ms |

- No 25-kt (12.86 m/s) threshold exists anywhere in `bridge/Crosscheck.Bridge.Core/Follow/`.
- GROUND VELOCITY is streamed and interpolated but never written; the follower's own read-back GS is not logged.
  The follower never writes VELOCITY BODY X or VELOCITY WORLD Y.
- Side note: the PF sends about 22 frames/s, not 30, because of the `1/StreamHz - 0.004` gate
  (`PhysicsSync.cs:385`) on a sim frame rate that is not a multiple of 30.

**Most likely cause.** The sim's ground and wheel model still runs on the frozen follower (friction, parking brake,
wheel speed). The GS that Fenix shows is computed by the sim or Fenix from that, not from the velocity we write.
The "25" is probably a sim or Fenix threshold, not ours. It only happens on the follower because only the follower
is placed. YourControls has the same issue (`docs/research/yourcontrols.md:415-441`).

**Proposed fix**
1. Log the follower's own `gs_kt` next to the PF's in `stream_stats` (no sim risk, just more data).
2. Then test on the ground: write the full body velocity (incl. BODY X) and/or GROUND VELOCITY while following.

**Sim test needed:** yes, for step 2.

## 4. MCDU desyncs

**Flight 1** – all caused by the blocked host keys S_CDU1_KEY_FPLN and S_CDU1_KEY_LSK2L (part A). The host's key
counter stayed odd ("pressed"), so every guest press was `press_lost` and then `stuck_pressed`. The host's 16
"Release it" clicks at 17:46:54–17:47:00 were refused because the var was blocked.

| Time | PF | What | Context | Cleared |
|---|---|---|---|---|
| 17:16:36 / 17:16:48 | handover | cannot_write, stuck_pressed FPLN | guest FPLN press | – |
| 17:17:02 / 17:17:07 | handover | cannot_write, press_mismatch LSK2L | after LSK1L ×2 | – |
| 17:17:13 / 17:17:15 | guest | press_lost + stuck FPLN, stuck LSK2L | scrolling, then FPLN | – |
| 17:17:25 | guest | mcdu1 display differs | after lost FPLN | 17:19:17 |
| 17:18:15 / 17:18:17 | guest | press_lost + stuck FPLN | | |
| 17:23:33 | guest | mcdu1 display | | 17:29:49 |
| 17:24:53 | guest | stuck LSK2L | host typing + LSK3L | |
| 17:25:58 / 17:26:01 | guest | press_lost + stuck FPLN | | |
| 17:28:19 | guest | mcdu2 display | | 17:28:21 |
| 17:35:21–17:35:47 | guest | press_lost LSK2L ×2, FPLN ×2, stuck ×3, mcdu2 17:35:35, mcdu1 17:35:41 | F-PLN page, DIR, LSK2L (a DIR TO) | not before 17:48 |
| 17:46:46 / 17:46:53 | guest | press_lost + stuck FPLN; 16 refused "Release it" | CLEAR, then FPLN | 17:48:02 (session end) |

**Flight 2** – no key problems (host PF, guest's CDU writes worked).

| Time | PF | What | Context | Cleared after |
|---|---|---|---|---|
| 18:08:04 | host | mcdu1 display | INIT B / fuel prediction entry (ZFW/ZFWCG, block fuel) | 42 s |
| 18:10:31, 18:16:34, 18:19:24 | host | mcdu1 display | no key presses in the 8 s before | 3 s, 4 s, 78 s |
| 18:22:58 / 18:23:00 | host | mcdu2 / mcdu1 display | none | about 4 min / 10 min |
| 18:26:58 | host | mcdu2 display | CLEAR ×2, INIT, ARROW_RIGHT on CDU1 | 5 min |

**Most likely cause.** Flight 1: part A (fixed). Flight 2: Fenix recomputes FMGC pages (predictions, fuel) on each
side on its own; with item 1 (different thrust and fuel) the predictions differ. The display is only logged as a
hash, so the page text is not visible.

**Proposed fix.** Re-fly with the part A fix. Log the differing MCDU lines (not just the hash) when a display
desync is raised, so the next flight shows which lines differ.
**Sim test needed:** no (re-fly is enough).

## 5. Cabin ready timing

**Evidence.** No cabin-ready var or message exists in the profile or the logs. The closest are the ASP CAB CALL
lights and the toilet light, which Fenix simulates on its own in each sim (server time):

| Episode | Host CAB CALL on → off | Guest CAB CALL on → off | Difference |
|---|---|---|---|
| F1 #1 | 17:21:28 → 17:23:16 | 17:21:40 → 17:23:16 | host 12 s earlier |
| F1 #2 | 17:47:38 → 17:48:04 | 17:46:54 → still on at 17:53:41 | guest 44 s earlier |
| F2 #1 | 18:15:24 → 18:17:12 | 18:14:49 → 18:17:01 | guest 35 s earlier |
| F2 #2 | 18:30:31 → 18:30:42 | 18:29:42 → 18:29:56 | guest 49 s earlier |

Toilet light: 10–37 s apart, plus extra toggles on the guest (17:57–18:03) not seen on the host.

**Most likely cause.** The cabin crew is simulated separately in each Fenix, on its own timers. It cannot be synced.
**Proposed fix.** Downgrade I_ASP*_CAB_CALL and I_OH_TOILET from red desync to an informational line (or drop them
from the comparison). If "cabin ready" is shown in Fenix's EFB/ECAM only, note it in the pilot guide as per-side.
**Sim test needed:** no.

## 6. Ground flight-control check

**Evidence.** While following, ELEVATOR, AILERON and RUDDER POSITION are written every frame, on the ground too
(`PhysicsSync.OnOwnFrame` → `PlaceLocked` → `PhysicsLink.Place`, `PhysicsLink.cs:79-92`). The only gate is "this
side is the follower". So the PM's sidestick and rudder inputs are overwritten and the control check fails.

**Proposed fix.** Do not write the three surfaces while the PF's frame says on ground **and** the PF's ground speed
is below 40 kt. Start writing them again when the PF's GS is 40 kt or more, or the PF is airborne; keep them on for
at least 5 s before stopping again (no flicker around 40 kt). The PF frame already carries `OnGround` and `gs_kt`.

**Safety.** Only affects the follower; it writes *less*, never more. Nothing changes on the PF. On the takeoff roll
the surfaces start following at 40 kt, well before rotation. Check for a visible jump when writing resumes.
**Sim test needed:** yes (control check on the ground, then a takeoff to see the hand-over at 40 kt).

## 7. Thrust lever range: read-only probe plan (not run)

Goal: the real range and detent values of `A_FC_THROTTLE_LEFT_INPUT` / `A_FC_THROTTLE_RIGHT_INPUT`, so Sebastian can
confirm a `range:` and the follower may write the levers (CLAUDE.md, follower placement rule).

**Design.** A new step `throttle` in the existing read-only `positions` command of `tools/profile-probe`
(same model as `positions`: the pilot moves the control, the tool only reads and records).
- Reads, every sim frame: A_FC_THROTTLE_LEFT_INPUT, A_FC_THROTTLE_RIGHT_INPUT, A_FC_THROTTLE_LEFT_GATE,
  A_FC_THROTTLE_RIGHT_GATE, WT_VIRTUAL_THROTTLE_LEVER_POS_1/2, XMLVAR_Throttle1/2Position, and the SimVars
  GENERAL ENG THROTTLE LEVER POSITION:1/2. Writes nothing.
- Preconditions it checks and refuses without: on ground, parking brake set, engines off (TURB ENG N1 < 1 %,
  already read by `SimvarReader.cs`).
- Prompts the pilot, one lever at a time, to move to and hold for 3 s: MAX REV, REV IDLE, IDLE, CL, FLX/MCT, TOGA,
  then a slow sweep IDLE → TOGA → MAX REV.
- Records per detent the plateau value (median of the hold) and the overall min/max; writes
  `profiles/fenix-a320/throttle-range-results.md` and a proposed `range:` for `overrides.yaml` (marked
  "to be confirmed by the owner").

**Commands (to run later, after Sebastian's OK; nothing installed, no WASM needed):**

```
cd tools/profile-probe
dotnet run -c Release -- plan
dotnet run -c Release -- positions --only throttle
dotnet run -c Release -- propose
```

**Sim test needed:** yes – this is the sim test. Read-only.

## 8. Remaining desyncs, grouped

Counts are unique desyncs (deduplicated by session and sequence number). All 175 `cannot_write` are part A.

| Reason | Var | Count | Real or noise | Why |
|---|---|---|---|---|
| display_mismatch | E_FCU_VS | 14 | noise | ±100 fpm while the knob turns; clears in about 3 s |
| display_mismatch | E_FCU_EFIS1_BARO | 6 | real – part A | host write blocked |
| display_mismatch | E_FCU_EFIS2_BARO | 3 | noise | 1 hPa, encoder timing |
| display_mismatch | E_FCU_HEADING | 3 | borderline | large values at session end (17:46–17:47) |
| display_mismatch | E_FCU_SPEED | 2 | noise | transient |
| display_mismatch | E_MIP_ISFD_BARO | 1 | real, minor | ISFD baro not synced |
| observe_mismatch | aircraft.mcdu1/2.display | 12 | flight 1 real (part A), flight 2 mostly noise | item 4 |
| observe_mismatch | I_ASP2/ASP3/ASP_CAB_CALL | 13 | noise | cabin simulated per side (item 5) |
| observe_mismatch | I_ECAM_CLR_LEFT/RIGHT | 10 | derived | ECAM state timing per side |
| observe_mismatch | I_OH_TOILET | 5 | noise | per side (item 5) |
| observe_mismatch | MASTER CAUTION / WARNING lamps | 16 | derived | shutdown cluster (below) |
| observe_mismatch | N_ELEC_VOLT_BAT_1/2 | 4 | noise | 28.6 vs 27.9 V; tolerance 0.5 is too tight |
| observe_mismatch | I_OH_INT_LT_EMER_OFF, I_OH_OXYGEN_CREW_OXYGEN_L | 2 | real – part A | guest write blocked |
| observe_mismatch | EFIS1/2 BARO STD + QNH | 4 | real | S_FCU_EFIS*_BARO_STD is `not_synced` (advisory ×4) |
| observe_mismatch | N_FCU_EFIS*_BARO_INCH, N_MIP_ISFD_BARO_INCH | 3 | real – part A | follows the blocked BARO |
| observe_mismatch | AUTOBRAKE LO, GEN1, PACK 1, ASP INT REC | 4 | derived | shutdown, transient |
| stuck_pressed | S_CDU1_KEY_FPLN / LSK2L / S_OH_RCRD_GND_CTL | 13 | real – part A | blocked writes |
| press_lost | same 3 vars | 11 | real – part A | blocked writes |
| toggle_mismatch | S_FCU_ATHR | 3 | real | A/THR state at disconnect/landing (item 1) |
| toggle_mismatch | S_ECAM_STATUS, S_ECAM_EMER_CANCEL, S_OH_HYD_YELLOW_ELEC_PUMP | 3 | derived | shutdown cluster |
| toggle_mismatch | S_OH_RCRD_GND_CTL | 1 | real – part A | |
| mode_mismatch | I_FCU_HEADING_MANAGED | 1 | real | heading managed differed 11 min (17:35–17:46) |
| mode_mismatch | B_FCU_VERTICALSPEED_DASHED | 1 | noise | 10 s |

**Shutdown cluster (flight 2, 18:32–18:41).** Master caution/warning, ECAM CLR, A/THR, autobrake, GEN 1, PACK 1 and
ENG MASTER 1 all differ here. S_ENG_MASTER_1 has 28 `hardware_fighting` advisories: both pilots moved it at
different moments. Treat it as one real event, not 20.

Other messages: `drift` 356 (all left-tank fuel, item 2); `advisory` 190 (`not_synced` 156 – standby DDRMI,
attitude cage, oxygen mask tests, EFIS BARO STD; `hardware_fighting` 30).

---

## Priority list

1. **Part A fix (done in code) and re-fly.** Removes all `cannot_write`, the stuck CDU keys, the speedbrake and
   BARO differences, and part of items 1 and 4.
2. **Surfaces off on the ground** (item 6) – small, safe, unblocks the PM's control check.
3. **Thrust lever range probe** (item 7), owner confirms the range, then write the levers on the follower (item 1).
4. **Noise downgrades:** cabin call / toilet lights, battery volt tolerance, E_FCU_VS display tolerance.
5. **CRO-41 logging:** follower's own GS in `stream_stats`, then the ground velocity test (item 3).
6. **BARO STD and heading push/pull sync** (`not_synced` advisories, mode_mismatch).
7. **Fuel/GW/brake observe values and the fuel-sync decision** (item 2) – after 1 and 3, the drift may be gone.
8. **CRO-43:** PF-computed values (N1, FMA, FD) for the PM, if 1–3 do not fix item 1.
