# CRO-22 – Gate test 2: diagnosis

Host (PF) and guest (PM) flew the Fenix A320 at the gate, offline, both on bridge 0.3.2 with the same profile hash
(`a9f52a1e…`), for 89 minutes. This document gives the cause of every item the pilots reported, using both bridge
logs, the profile and the code. For each item it also says whether the pilots were shown it. **No code was changed.**

Sources: the host log and the guest log (bridge JSONL, ~76,000 / ~79,000 lines), `profile.yaml`, `unsynced.csv`,
`SyncEngine.cs`, `SimSafetyGuard.cs`, `server/src/session/`, `ui/src/screens/Session.tsx`. Times below are the host's
(UTC). The guest's clock ran about 10 s ahead.

What worked: covers, most latching switches and selectors, the positions added after the positions run (flaps lever
aside, see H), ECAM page keys, MCDU typing at normal speed, ASP REC latches, clock ET/UTC, standby altimeter QNH
(12/12 acked), clock SET knob turns, the FCU turn knobs at normal speed (display matched; see E for the false reds).

## 0. In short

1. **Safety finding:** Crosscheck held the **IDG 1 and IDG 2 disconnect buttons pressed down in both sims** from
   19:29 until the end. The guest got it from the first mirrored click; the host got it from the panel's
   "Set both" button. No IDG actually disconnected, most likely because the engines were off. Cause A.
2. Of 224 red lines, only **31 were unique, real "your input didn't arrive" cases** (about 24 controls). The other
   193 were false, made by Crosscheck's own corrections, repeats of an open line, or side effects (section 3).
3. The ALT run to 49000/00100 was **Crosscheck's own correction**: it turned the display difference into the same
   number of knob clicks (for example −37,100 clicks in one write). The ALT 100/1000 selector was the same on both sides.
4. Fast presses drift because the receiving bridge sometimes reads its own value before its last write has
   landed. It then thinks the local pilot is holding the button and drops a press.
5. Gate test 1 cause H (encoder check too strict) was never built: 51 false reds.

## 1. Your quick read

| Your point | Verdict | Evidence |
|---|---|---|
| 250 desyncs, 224 red | **Confirmed** | 117 refused, 33 correction_failed, 32 effect_mismatch, 16 mode_mismatch, 13 correction_loop, 11 toggle_mismatch, 1 safety_release, 1 press_mismatch; 26 quiet |
| Most reds are encoder "refused" (cause H never fixed) | **Partly.** Cause H is confirmed as not built (`SyncEngine.cs:1131`, `:1192`: exact read-back within 250 ms). But the encoder refusals are 51 of 224 reds (HDG 27, VS 10, SPD 7, BARO 6, ALT 1): the largest single group, not most of them | Every one of these lines closed within 0.001–0.9 s, and the displays matched |
| ALT correction_loop because S_FCU_ALTITUDE_SCALE differed | **Loop confirmed, cause rejected.** The selector was 1 on both sims at the start. The host set it to 0 at 20:00:31 and the guest followed; it went back to 1 on both. The real cause is F: a correction writes the display difference as clicks. At 18:51 the guest FCU was also unpowered (cold and dark), so its display never moved | Guest log: `delta_write change 900` (×6), then `change -37100`, then `+11800`. The display slammed to 49000, 100, 49000 |
| Encoders must never be corrected into a loop | **Agreed, and further:** encoders should not be corrected automatically at all (see F) | – |
| FD (17) and LS (8) effect_mismatch after fast presses | **Confirmed.** These 25 lines are flicker: each closed within 0.02–0.23 s. The real result is the lasting toggle_mismatch (FD 29 s; LS 1 for 20 min; LS 2, CSTR and WPT until the end). The cause is D | Guest log 19:58:13: `edge_already_down` / `edge_up_skipped` |

## 2. Causes, grouped

### A. Push-button counters profiled as switches, and a "Set both" button with no deny list – **safety**

`S_OH_ELEC_IDG1/2`, `S_PED_COCKPIT_DOOR_VIDEO` and `S_FCU_EFIS1_BARO_STD` are Fenix counters (+1 per press and +1 per
release; BARO STD counts down on pull). They are profiled as `verified` with `values: [0, 1]`.

- **IDG 1/2 – "only the first click".** The host clicked: values 1, 2 … 22 (IDG 1) and 1 … 16 (IDG 2).
  - The guest accepted the first value, **1 = odd = held down**, and refused every later one (84 + 60 refusals, 12
    lines). The guest's IDG buttons stayed pressed from 19:29:41.
  - At 19:34:33 the host used the panel's **"Set both to 1"** on both lines, which wrote 1 (held) into the host sim too.
  - Final snapshot: IDG 1 = 1 and IDG 2 = 1 on **both** sims.
  - Why it was allowed:
    - "Set both" (`SyncEngine.PilotSetAsync`, `:1310-1329`) only checks class and allowed values. It has no role
      check.
    - `SimSafetyGuard` has **no deny list**: fire buttons, IDG, ENG MASTER and the like get no special treatment.
  - Shown? Yes, as red refused/correction_failed lines. Those lines invited the dangerous "Set both" action.
- **COCKPIT DOOR VIDEO** (pedestal). Values 1 … 86, 321 refusals on the guest, 2 correction_loops, red for 39 min.
  Probably the "cabin camera screen" in the misc list.
- **BARO STD (EFIS 1).** Values −9 … 1.
  - Already at the session start the server tried to "correct" the guest to −9 (refused).
  - Later pulls were refused, so STD did not sync. Red for 37 min.
  - The pilots used "Set both to 0" twice.

**Fix:**
1. A profile **deny list** (`never_write: true`), irreversible controls only (owner decision 2026-11-02):
   - IDG 1/2 disconnect
   - ENG 1/2 and APU fire push buttons
   - every AGENT/DISCH button (engine, APU, cargo)
   - RAT MAN ON
   - passenger oxygen MAN ON

   Not on the list: ENG MASTER, DITCHING and EVAC, which are reversible (ENG MASTER must keep syncing). The guards
   (covers) keep syncing. The guard refuses deny-list vars on every write path (command, correction, "Set both",
   "Press here only", safety release). They are watched and shown ("IDG 1 pressed on the host – not mirrored,
   irreversible"), never written. How to mirror them later is CRO-31.
2. Re-profile the three counters as `pulse` + `press: edge` (door video, BARO STD) after a short read-only observe.
3. "Set both" only for proven absolute switches and analog knobs. Never for a var whose values were seen above its
   allowed set: that pattern means a counter.
4. A detector: a `verified` var whose values keep rising in steps of 1 raises "probably a press counter – not
   synced" (amber) and blocks writes.

### B. A 0/1 latch profiled as a counter – **silent** (BLUE ELEC PUMP)

The host's `S_OH_HYD_BLUE_ELEC_PUMP` toggled 1 → 0 → 1 → 0 → 1. That is a latch, like PRESS MODE SEL and FUEL X FEED.
The profile has it as `press: edge`, so the guest (at 1) logged `edge_already_down` / `edge_up_skipped` for every click
and **acked all of them ok**. The lamp check passed, because the lamp was 0 on both sides. Both sims ended at 1.
Not shown.

**Fix:** profile as `verified [0, 1]` with the lamp as the check. Also, an edge receiver must never ack ok after
skipping a press it could not explain: an "already down" with no local press seen is red.

### C. Two pilots on one counter button flip it to "pressed" (APU AUTO EXTING RESET – "inverted")

At 18:54:34 the guest clicked RESET while the host's press was being mirrored into the guest sim.
- Fenix counters only ever add 1, so the guest's click turned the mirrored "down" into "up". From then on the
  guest's button **rested at an odd value (pressed)**: each of his clicks arrived as an up edge first, then a down.
- The host side got a mirrored down with no up and was **released by the 30 s safety cap** at 18:55:10 (red).
- `press_mismatch` ("held down in one cockpit only") was then red from 18:55:15 to the end (84 min). The guest's
  final value was still odd.
- The pilot cannot fix this by clicking: a click adds two.

**Fix (owner decision 2026-11-02):**
- Detection: a counter that rests odd for more than a few seconds while neither bridge sees a pilot holding it is
  "stuck pressed" (red).
- Recovery is **pilot-initiated, never automatic**: the red line offers "Release it". It writes **one up step** (+1)
  in the cockpit where the button is stuck, only when no pilot holds it on either side. Never a down. This extends
  the narrow exception in the hard safety rules; `CLAUDE.md` and `docs/protocol.md` section 5 get the wording when
  it is built.
- Prevention: incoming edges for a button are held back while the local pilot's own press on it is in progress.

### D. Fast presses lost on the receiver (FD, LS, CSTR/WPT, PFD/ND XFR, FO chrono, MCDU key 0)

The host pressed FD 9 times in 1.2 s (edges 41–87 ms apart). On the guest at 19:58:13.084 a down edge found the own
value still odd (43), because the guest's previous up write (44) had not been read back yet (read 3 ms later).
- The receiver took it as "local pilot holding" (`edge_already_down`) and skipped it. The matching up was then
  `edge_up_skipped`. One press was lost, so the lamp parity flipped.
- The ack for the lost press was **ok**.
- Code: the down rule reads the cached sim value (`SyncEngine.cs:931-956`) and does not count its own pending write.

Same pattern in the guest log: FD 8 times, LS 1, CDU brightness 4, AUTO BRK LO 1, FO chrono (20:02:33), and
**MCDU key 0 at 20:16:17**. The lost 0 explains the wrong THR RED.
- PFD/ND XFR has no lamp check, so its drift was **silent**. The chrono only showed as drift, also silent.
- FD/LS/CSTR showed as red toggle_mismatch (correct).
- The 25 effect_mismatch flickers came from checking the lamp in the middle of a press burst.

**Fix:**
- Per var, the receiver keeps the value it last wrote and judges new edges against that, not against a stale
  read.
- Edges queue strictly in order: an edge waits for the previous edge's read-back.
- The effect check runs once after the burst settles (last edge + 400 ms), not per edge.
- An `edge_already_down` with no local press seen is a red "press lost", not ok.

### E. Encoder check too strict (gate test 1 cause H, never built)

`ok = stored`, where `stored` needs the exact counter target within 250 ms (`SyncEngine.cs:1131`, `:1192`). Fast turns
move past it.
- 51 refused lines on HDG/VS/SPD/BARO/ALT, open 0.001–0.9 s each. All were closed by the next ok ack, and the
  displays matched.
- Shown as flicker.

**Fix (as planned in gate test 1):**
- Step 1 passes when the counter reached or passed the target in the turn direction.
- The proof is the display after settle.
- No red per click: one line only if the display still differs 2 s after the last click.

### F. Encoder corrections write the display difference as clicks – **Crosscheck made it worse**

The server sends the PF's **display** as the target. The bridge writes `counter + (target − display)`
(`SyncEngine.cs:881-898`): one click per display unit.
- **ALT:** 100 → 1000 became +900 clicks, and the FCU was unpowered on the guest, so nothing moved. This was repeated
  6 times, then a correction_loop red at 18:51 that stayed **for 88 min**. That is the pilots' "altitude indicators
  differing while the aircraft was off".
- At 20:00: −37,100 clicks (display to 100), then +11,800 (to 49000).
- **VS:** 5900 vs 0 gave thousands of clicks. The guest VS swung between ±6000 four times.
- **RMP 1 standby:** the host pressed HF1/HF2 (not synced), so its standby display showed an HF frequency (8.900).
  The correction tried to turn the guest's VHF display there and drove it to the bottom stop, 118.000. Red for 32 min.
- **Phantom turns:** the guest sent 13 HDG/VS "turns" back to the host within 400 ms of applying the host's own
  clicks. Each was an intermediate read-back taken as a pilot turn (`flushed: true`).

**Fix:**
- **Never correct an encoder automatically.** A display difference becomes one red line: "ALT differs: yours
  11900, other 49000. Turn the knob in the cockpit that is wrong."
- Don't compare FCU/RMP displays while the unit is unpowered or in a different mode (RMP: compare only when both
  have the same selected radio).
- Fix echo handling so an intermediate value of our own write is never sent as a pilot turn.

### G. Allowed values and ranges still too narrow (→ CRO-24)

Valid pilot positions refused by the guard on the other side:

| Item | Profile | Seen | Lines |
|---|---|---|---|
| ICE IND & STBY COMPASS (`S_OH_IN_LT_ICE`) | [0] | 0, 1 | refused 4, failed 1 |
| CONSOLE/FLOOR light | [0, 1] | 0, 1, 2 (BRT) | refused 2 |
| WX TILT | 3 … 10 | −15 … 15 | 59 refusals, 7 lines; guest stuck at the last value inside the range |
| WX GAIN | −5 … 0 | −5 … +4 | 21 refusals, 5 lines (plus side) |
| LDG ELEV | −3 … 7 | −3 … 9.75 | 21 refusals, 1 line (not reported) |
| Chart light temp | [0] | 0, 1 | 16 lines incl. "hardware fighting" |
| Cargo door/bay light selectors (walkaround) | [0] | 0, 1 | 5 vars, red for 43 min |

Shown: yes, red, many repeats. Two "hardware fighting" lines were false: the refusals made the values flip.
**Fix:** CRO-24 learn mode (section 6). Interim: add the seen values (owner confirms).

### H. Writes that don't stick (value written, read back as the old value)

> **Corrected after gate test 3 (CRO-40).** The cause below was guessed wrong. Every var in this table sat late
> in the bridge's list of writable vars; the bridge registered one SimConnect data definition per writable var
> (about 1,180 then), and the sim refused those past roughly 900–1,190 without anything in the session log. Writes
> to them never landed, in both directions. Not Fenix putting the value back, not a hardware lever, not
> hydraulics, and not the sim's handle states. See [the gate test 3 diagnosis](CRO-22-gate-test-3.md), section 2A.
> Fixed in bridge 0.5.0 (a pool of write definitions; a refused write is a red line). The original text stays
> below as it was written.

| Item | Sent → read back | Notes |
|---|---|---|
| Spoilers (`A_FC_SPEEDBRAKE`) | 1.0 … 3.0 → always 1 | 206 failed acks. The guest sent no commands back, so its lever never moved: Fenix (or the sim's handle) puts it back every frame |
| Flaps (`S_FC_FLAPS`) | 0 … 4 → always 0 | 10 failed acks; **no red** (each newer command replaced the old one), so effectively silent |
| A/SKID & N/W STRG | 0 → 1 | Both directions failed (3 refused lines, host and guest) |
| RUD TRIM switch (`rest: 1`) | 0/2 → 1 | Fenix springs it back within the frame; the receiver's trim did not move |
| RUD TRIM RESET | edge → no read-back | 8 lines; the host counter is 13, the guest's 1, so it is not a normal +1/+1 counter |
| COCKPIT DOOR (`S_COCKPIT_DOOR`) | 0 → 1 | Both sides |
| Window lever, rear blind, galley breaker cover, EXT MAINT BUS | 1 → 0 | Window lever probably needs its button (unsynced) first |

Shown: red refused for most; flaps only as failed acks.
**Fix:** one read-only observe run per control (owner approval) to find the var Fenix really reads. For flaps,
spoilers and A/SKID the likely candidates are the sim's handle/switch states rather than the L: var. Until then,
move them to "not synced yet" (amber), so nobody expects them to work. Hardware on the guest side: to confirm
with the pilots, but the logs show no movement from it.

### I. Still on the review list (amber "not synced yet", never written)

ENG N1 MODE 1/2, RMP keys HF1/HF2/AM/NAV/VOR/LS/GLS/ADF/BFO, ECAM upper/lower brightness, PFD/ND brightness
(`A_DISPLAY_BRIGHTNESS_CI/CO`), CPDLC (DCDU) BRT/DIM, AUTO BRK MED/MAX, CLOCK SET (the selector; the set knob turns
did sync), analog standby instruments (DDRMI, attitude cage/flag), pedestal COCKPIT DOOR switch, gravity gear
handle, oxygen mask hatch and TEST, window button, CCTV, standby compass, EFB cable.
- Shown: yes, as 40 amber "not synced yet" lines.
- These lines **never expire** (no `active:false` is ever sent for `not_synced`).
- **Fix:** classify with profile-probe (positions/observe, read-only), with brightness knobs and RMP keys first.

### J. Unclear

- **Volume control panel.** The host turned six ASP 1 volume knobs. All synced and acked ok (2 of 20 MLS acks failed
  on exact read-back). The guest sent no volume command at all. Possibly the two pilots compared different panels
  (ASP 1 vs ASP 2): to ask.
- **WX TILT "mine 15, his stops at 5".** The guest refused everything above 10 and below 3. The numbers the pilots
  read are probably the knob scale, not the L: value. Fixed by G either way.

## 3. Every red, by kind

| Kind | Lines | Why it was red |
|---|---|---|
| **False – nothing was wrong** | **87** | |
| Encoder exact check (E) | 51 | Counter had moved on; the display matched |
| Lamp checked mid-burst (D) | 32 | effect_mismatch FD 17, LS 8, SVCE INT OVRD 6, CSTR 1; each closed in under 0.25 s |
| ECAM CLR lamp as a toggle | 4 | The lamp follows ECAM, not the button (observe report) |
| **Made by Crosscheck's corrections (F, A)** | **13** | correction_loop ALT 3, VS 3, RMP 3, HDG 1, BARO 1, door video 2 |
| **Repeats of an open line** | **75** | 33 correction_failed after a refused; 42 further refused lines for controls already red |
| **Side effects** | **18** | 4 FCU managed lamps × 4 events (19:57–19:59): these appeared and cleared with the FD presses, with no push/pull seen then. Plus ECAM ENG and WPT (follow ALL/CSTR) |
| **Real, unique** | **31** | 15 refused by range or values (G, A), 9 writes that did not stick (H), FD 1, LS 3, CSTR 1, APU RESET 2 |

Of the quiet lines (26), the useful ones were MCDU screen (1) and cargo door lamps. Battery volts and brake
pressures were noise, as already proposed in gate test 1 (tolerances, not built).

What the pilots had on screen: up to **19 red cards at once** (20:08), and 17 were still open at the end. ALT was red
for the whole session. The Recent list cycled through 483 raw rows (opened/cleared/corrected, raw var names), and
40 amber lines never went away.

## 4. Proposal: a conservative, clear panel

Rules
1. **One line per control,** not per var. Group: button + its lamp; knob inner + outer + display; FCU mode lamps
   under their knob; walkaround items as one "outside" control. Plain labels, never raw var names.
2. **Red only when a pilot can act, and it has lasted.** A pilot input did not arrive, and the control still
   differs 2 s after the last movement (encoders: after the turn ends; buttons: after the burst).
   - Never red for: lamps during a press burst, items outside the cockpit, effects of another red line, or anything
     Crosscheck caused itself.
   - Nothing is compared while that unit is unpowered.
3. **Every red line says what to do,** with one action. "Set both" only for proven absolute switches and analog
   knobs; never for push buttons, counters or deny-list items.
4. **No automatic corrections of encoders, buttons or anything the pilots can see moving.** If a difference
   remains, it is a red line for the pilots to fix.
5. **Everything else collapsed:**
   - One amber line "Not synced yet: N controls (moved in the last 2 min)" that expires.
   - One amber line per hardware fight or drift.
   - One grey line "Indicators differ (n)".
   - The Recent log moves behind "Details".

Line templates

| Situation | Line (example) | Action |
|---|---|---|
| Switch/selector differs | **STROBE** – you ON, other AUTO. | "Set both to ON" / "Set both to AUTO" |
| Toggle button lamp differs | **LS (Capt)** – lit here only. | "Press LS here only" (one click, not mirrored) |
| Encoder display differs | **FCU ALT** – you 11900, other 49000. | "Turn ALT in the cockpit that is wrong" (no button) |
| Button stuck pressed | **APU AUTO EXTING RESET** – stuck pressed in the guest cockpit. | "Release it" (one up step there, pilot-initiated, see C) |
| Not synced by design / not yet | **IDG 1** – pressed on the host. Not mirrored (irreversible). | "Do the same in the other cockpit if intended" |
| Write doesn't stick | **FLAPS** – Crosscheck cannot set this yet. | "Set it by hand in both cockpits" |

Grouped this way, gate test 2 would have shown at most 6–8 red lines at a time instead of 19. ALT would have been one
line asking for a manual turn, and the false and repeated lines (175 of 224) would not exist.

## 5. By design, and the misc items

- **By design:** throttles (ignored until the stream, CRO-17; no throttle command was sent, correct). Cosmetic cabin
  items: seats, armrests, tray tables, window shades/blinds, jumpseat, EFB cable, CCTV/cabin camera screen, standby
  compass deploy, oxygen mask hatch. The window handle and window movement are each pilot's own (owner decision
  2026-11-02). Move all of these to `ignore`, so they stop producing amber lines.
- **Should sync:** gravity gear extension (handle and crank: a system state, reversible) and the cockpit door
  (pedestal LOCK/NORM/UNLOCK switch with `rest`, and the door itself once a write is proven to stick, see H;
  corrected: the door write was the bridge's definition limit, gate test 3 section 2A).
- **Not synced, each pilot's own:** the crew oxygen mask TEST (never pressed in tests).
- **Irreversible, with CRO-31:** IDG 1/2 disconnect, like the fire push buttons, every AGENT/DISCH button, RAT MAN ON
  and passenger oxygen MAN ON. They are on the deny list now (A). How to mirror them is CRO-31's decision.
- **Flaps and spoilers** are not "maybe hardware" by design: they are cause H, to be solved (solved in gate test
  3: the bridge's definition limit, section 2A there).

## 6. Fix plan, in priority order

1. **P0 – safety.**
   - Deny list in the guard (IDG 1/2, ENG/APU fire push buttons, all AGENT/DISCH, RAT MAN ON, passenger oxygen MAN
     ON), enforced on every write path, watched and shown.
   - "Set both" only for proven absolute switches/analog.
   - Pilot-initiated "Release it" for stuck-pressed buttons, plus holding back edges during a local press (C).
2. **P0 – stop Crosscheck making it worse.** No automatic encoder corrections (F). No comparison of unpowered units.
   Fix encoder echo (phantom turns).
3. **P0 – panel** (section 4): one line per control, a 2 s hold, an action per line, deduplicated, expiring amber,
   Recent behind Details.
4. **P1 – edge receiver race (D):** judge edges by the last written value; queue in order; one effect check after
   the burst; "press lost" is red, never ok.
5. **P1 – encoder check (E):** reached-or-passed, display after settle.
6. **P1 – profile fixes:** BLUE ELEC PUMP → latch (B). Door video and BARO STD → edge after a short observe (A). ECAM CLR
   lamp is not a toggle check. Counter detector (A4).
7. **P1 – CRO-24 learn mode.** It fixes the range and position items now:
   - ICE IND & STBY COMPASS
   - CONSOLE/FLOOR BRT
   - WX TILT, WX GAIN, LDG ELEV
   - chart light temp
   - cargo/walkaround selectors
   - later, the brightness knobs and standby instruments once they are classified

   It does **not** fix counters profiled as switches (A, B), writes that don't stick (H), the edge race (D),
   encoders (E, F) or review-list items (I).
   **Guard for CRO-24:** never learn values for a var whose values keep rising (a counter). Learning IDG's 2 … 22
   as "positions" would have made writing them legal.
8. **P1 – read-only observe runs** (owner-approved): flaps, spoilers, A/SKID, RUD TRIM and RESET, cockpit door,
   window lever (not needed for these after gate test 3: cause H was the bridge's definition limit), N1 MODE, RMP keys, AUTO BRK MED/MAX, brightness knobs, DCDU BRT/DIM, door video, BARO STD.
9. **P2 – tolerances and power gating for indicators** (gate test 1 section 4, still open). `not_synced` advisories
   expire.

### Verification for the fixes
- Replay tests from this log:
  - FD burst with 41 ms gaps: no press lost.
  - IDG click: the guard refuses, a watched line is shown.
  - ALT correction case: no write, one red line.
  - BLUE PUMP latch toggles.
  - Two pilots on one counter button.
- UI test: the gate test 2 desync stream renders at most one line per control.
- Gate test 3 with the same checklist.

## 7. Owner decisions (2026-11-02)
1. **Stuck-pressed recovery (C):** yes, pilot-initiated only. A "Release it" button on the red line writes one up
   step in the cockpit where the button is stuck, only when no pilot holds it on either side. Never a down. Plus the
   prevention: hold back incoming edges while the local pilot's own press is in progress.
2. **Deny list (A):** irreversible controls only:
   - IDG 1/2 disconnect
   - ENG 1/2 and APU fire push buttons
   - every AGENT/DISCH button (engine, APU, cargo)
   - RAT MAN ON
   - passenger oxygen MAN ON

   Not ENG MASTER, DITCHING or EVAC: they are reversible, and ENG MASTER must keep syncing. Guards (covers) keep
   syncing. Deny-list controls are watched and shown, never written, on every write path.
3. **Window handle and movement:** each pilot's own (`ignore`).
4. **Volume panel and WX TILT (J):** still open; the answers will come from the pilots.
