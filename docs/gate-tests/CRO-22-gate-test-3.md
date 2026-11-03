# CRO-22 – Gate test 3: diagnosis

Host (PF) and guest (PM) used the Fenix A320 at the gate, offline. Both were on bridge 0.4.0 with the same profile
hash (`b2d6d47a…`), for about 25 minutes (guest joined 19:29:31, left 19:47:36). This document gives the cause of every
item the pilots reported, using both bridge logs, the profile and the code. It confirms what now syncs from gate
test 2, lists the differences that showed no red line, and counts the red lines by kind. **No code was changed.**

Sources: the host log and the guest log (bridge JSONL, ~20,600 / ~17,000 lines), `profile.yaml`,
`SimConnectAdapter.cs`, `SyncEngine.cs`, `EchoTracker.cs`, `server/src/session/`, the supervised write test
(`profiles/fenix-a320/writetest-results.md`), and the gate test 2 logs for a cross-check. Times below are the host's
(UTC). The guest's clock ran about 10.5 s ahead.

Pilot report: the panel was much better, with no noise. Everything worked except flaps, speed brake, rudder trim and
RESET, cockpit door, oxygen mask cover, seat switches, armrests and tray table.

## 0. In short

1. **Every control the pilots reported is one cause, and it is ours, not Fenix's.** The bridge registers one
   SimConnect data definition per writable var (1,238 of them). Every write to a var early in that list worked.
   Every write to a var late in it failed. The same cut explains all of gate test 2 cause H. The cause is most
   likely a SimConnect limit on data definitions per client (section 2A).
2. Fenix does not overwrite these L: vars. The written value never appeared, even for one frame. The supervised
   write test, run with a separate tool that registers only a few definitions, wrote flaps, speed brake, A/SKID and the
   cockpit door, and they stuck.
3. **The panel was calm:** at most 6 red controls at once (gate test 2: 19). There were **no false red lines**
   (gate test 2: 100).
4. Everything from gate test 2 that the pilots used now syncs: deny list, BLUE pump, APU RESET, fast presses,
   encoders (no corrections, no false reds), N1 MODE, RMP keys, DCDU BRT/DIM, brightness knobs.
5. One silent gap: the **rudder trim position** is not compared, so after the short switch red lines it differed with
   no line. Elevator trim was sent outside its range: make it `ignore` until the stream (CRO-13).

## 1. Your quick read

| Your point | Verdict | Evidence |
|---|---|---|
| The write is overwritten by Fenix within a few hundred ms; the L: var is an output | **Rejected** | The failures follow the var's position in the bridge's write list, not the control (2A). The bridge reads every frame and never saw the written value, not even for one frame (`stored: false` after the 250 ms read-back window), so it was never "put back": it never landed. The supervised write test wrote the same vars and they stuck |
| Mask cover close 5/5 ok, open 5/5 fail | **Confirmed as data, cause rejected** | "Close" was the value the cover already had, so the read-back passed without any write landing. The same holds for every "ok" past the cut: tray 0, armrest 0, seat 1, door 1, flaps 0, and flaps 4 once, only because the guest pilot had just set 4 |
| Flaps stuck in the supervised write test because there was no hydraulic pressure | **Rejected** | The write test tool registers only a few definitions, so its writes land. The tray table and mask cover have nothing to do with hydraulics, and they failed the same way here |
| Input events (B: events, `EnumerateInputEvents` / `SetInputEvent`) as the input path; a read-only enumerate step | **Not now** | The L: var path works once the write is registered. Input events would also be a new kind of write, which the adapter's safety tripwire forbids (it sends no events of any kind). Keep this only as a fallback if a control still does not stick after fix 1 |
| A_FC_ELEVATOR_TRIM sent as 100 and refused 55 times (range [0, 1]): make it ignore | **Confirmed, with one correction** | The host sent **54 rising values, 100 … 5400** (the trim wheel moving, steps of 100 about every 260 ms), plus one server correction to 5400. The guard refused all 55. The range [0, 1] was a guess from the scan (`A_ knob`). Make it `ignore` until the stream (CRO-13), like `N_FC_ELEVATOR_TRIM` (CRO-27) |

## 2. Causes

### A. The bridge cannot write vars late in its list – every reported control

`SimConnectAdapter` gives every writable var its own SimConnect data definition at connect
(`SimConnectAdapter.cs:40-41`, `:161-165`): 1,238 write definitions in this profile, on top of 57 read definitions.
SimConnect reports a problem with a definition asynchronously, and the bridge logs it only to its console
(`:145`), not to the session log. So nothing in the logs says a definition was refused. The pattern is clear
anyway:

| Write list position | Vars written in the session | Result |
|---|---|---|
| 0 – 813 | 72 (overhead, FCU, EFIS, MIP, pedestal, ECAM) | all stored (a few misses on fast knob turns, as expected) |
| 1174 – 1209 | 16 (flight controls, cabin, walkaround) | **every write failed**, except a value the sim already showed |

The failing vars, from the receiving side's results:

| Control | List position | Failed | "Ok" (value already shown) |
|---|---|---|---|
| Speed brake `A_FC_SPEEDBRAKE` | 1174 | 34 | 5 (value 1 = RET) |
| Flaps `S_FC_FLAPS` | 1175 | 24 | 4 (0, and 4 once) |
| RUD TRIM `S_FC_RUDDER_TRIM` | 1177 | 10 | 10 (1 = rest) |
| RUD TRIM RESET | 1178 | 33 | 1 |
| Armrest | 1181 | 4 | 3 (0) |
| Cockpit door `S_COCKPIT_DOOR` | 1183 | 3 | 2 (1) |
| Jumpseat headrest | 1187 | 2 | 1 (1) |
| Oxygen mask cover | 1188 | 6 | 5 (0) |
| Seat distance / height | 1190, 1192 | 12 | 4 (1 = rest) |
| Tray table | 1195 | 3 | 2 (0) |
| Walkaround cargo lights and selectors (5 vars) | 1202 – 1209 | 10 | 0 |

**Worked example, flaps at 19:37.** The guest pilot set flaps 4 (19:37:35). The host's lever went 1, 2, 3, 4 and
back. On the guest, 1, 2 and 3 failed and **4 was "ok"**, only because the guest was already at 4. In the other
direction, the host refused 4 from the guest. Each side's flaps moved only when its own pilot moved them.

**Cross-check with gate test 2** (older profile, its own list positions):

| Write list position | Result |
|---|---|
| 0 – 773 | all stored |
| 1131 – 1168 | failed: speed brake, flaps, A/SKID, RUD TRIM, RUD TRIM RESET, cockpit door, galley breaker cover, rear blind, window lever, EXT MAINT BUS |

That is exactly the gate test 2 cause H list. It was never Fenix, a hardware lever or missing hydraulics.

**Where the limit is.** In both tests the cut lies between about 870 and about 1,190 definitions in total (read +
write). The SDK documentation found locally gives no number. Finding the exact limit takes the registration-only
check in section 7 (no writes).

**Why the earlier conclusions were wrong.** In the supervised write test the tool registered a few definitions, so
its writes landed. That report then blamed "something in the receiving cockpit", and gate test 2 blamed the L: var
not being Fenix's input. Both are superseded by this finding.

Shown? Yes, as red lines (61, section 5). But the panel offered "Set both" for flaps and speed brake, and the pilots
used it 3 times (19:37:57, 19:37:59, 19:41:39). It could not work.

### B. Elevator trim profiled as a 0 … 1 knob

`A_FC_ELEVATOR_TRIM` has `range: [0, 1]`, from the scan's guess for an `A_` var. The host's trim wheel sent 100,
200 … 5400. The guest guard refused every one (19:46:35 – 19:46:41), and the server then sent a **correction with
target 5400**, outside the profile range. The guard refused that too, as it must, but the server should never send
it. Red until the end. The trim position var `N_FC_ELEVATOR_TRIM` is already `ignore` until the stream (CRO-27).

### C. Allowed values: cargo door selector AFT

The guest moved `S_EXT_CARGO_DOOR_SELECTOR_AFT` to 1. The host's guard refused it (allowed values are [0]). This is a
walkaround item, so the panel showed it only in the amber "Outside the aircraft" line. Its neighbours failed by
cause A.

### D. CREW OXYGEN flipped about 6 times a second on the guest

From 19:31:40 to about 19:31:53 the guest's `S_OH_OXYGEN_CREW_OXYGEN` flipped 0/1 every ~150 ms with no host input. A
mouse wheel turned over the button would do that. The host applied the stream, and its bridge also sent part of it
back as its own moves, so the server saw an override fight. Shown as amber "hardware fighting" (24 updates). Both
sides ended equal. The pilots did not report it. Whether the host's moves were echoes of our own writes needs a
replay test from this log.

### E. F/O BARO changed on the guest without a knob turn

At about 19:43:39 the guest's F/O BARO went from 1013 to 994. Its knob counter did not move, so no turn was sent.
994 is probably the sim's live QNH on the guest machine, set by a sim "set altimeter" key or binding. The panel showed it
**red after 6 s** ("turn the knob in the cockpit that is wrong"), correctly. It was still open at the end.

### F. ECAM and MASTER lights on the guest only

From 19:37:51 the guest's speed brake stayed at 3 while the host's was at RET (cause A). The write test showed that
a raised speed brake lights MASTER CAUTION and ECAM CLR, and that is what the guest got. Later, while the flaps also
differed, MASTER WARNING and the ECAM ENG and STS page lamps followed on the guest only. These are follow-on effects
of cause A, not causes of their own.

## 3. Everything from gate test 2 – does it sync now?

| Gate test 2 cause | Gate test 3 | Verdict |
|---|---|---|
| A – deny list, counters, "Set both" | CARGO DISC 1 and 2 (agent buttons) pressed on the host at 19:25:27: red "not mirrored" to both pilots, never written. IDG not pressed. "Set both" used only on flaps and speed brake | **Works** (but see 2A for "Set both") |
| B – BLUE ELEC PUMP latch | 2 of 2 ok | **Syncs** |
| C – APU AUTO EXTING RESET stuck pressed | 2 of 2 ok; no stuck-pressed line, no safety release | **Syncs** |
| D – fast presses lost | ECAM CLR 276 edges, PFD/ND XFR 62, MASTER CAUTION 34, MASTER WARNING 16: all ok. No lamp check mid-burst, no press lost | **Syncs.** FD, LS, CSTR and MCDU typing were not used |
| E – encoder check too strict | No encoder red line. ALT 243 of 260 clicks and SPD 191 of 204 stored at once; the misses were not red, and the displays matched | **Fixed** |
| F – encoder corrections, phantom turns | No encoder correction sent, no correction loop. The guest's 124 ALT/SPD turns were real: both pilots turned SPD in opposite directions at the same time (19:32:10–16). Each click went out once, and the displays ended equal | **Fixed** |
| G – ranges too narrow | Walkaround items are now one amber "outside" line. WX TILT/GAIN, LDG ELEV, ICE IND, CONSOLE/FLOOR were not used | Partly checked |
| H – writes that don't stick | Same cause as 2A, now explained | **Explained, not fixed** |
| I – review list | N1 MODE, RMP keys, DCDU BRT/DIM, ECAM and PFD/ND brightness, pedestal COCKPIT DOOR switch: all ok. "Not synced yet" amber lines now expire (48 expiries) | **Syncs** |

Not used in gate test 3, so still to check: MCDU typing, FD/LS/CSTR presses, V/S, HDG (2 clicks only), RMP
frequency knobs, WX TILT/GAIN, LDG ELEV, BARO STD, cockpit door video, A/SKID, AUTO BRK MED/MAX, clock SET.

## 4. Silent differences (a difference with no red line)

1. **Rudder trim position.** `N_FC_RUDDER_TRIM_DECIMAL` is `ignore` (not compared until the stream, CRO-27). The
   RUD TRIM switch reds lasted 1–2 s each. After that, the host's trim stood away from the guest's with no line.
   **The one real gap.** Owner decision: compare it as an amber indicator (section 8).
2. **MASTER CAUTION and ECAM CLR** (19:37:51, 90 s) and **MASTER WARNING and CAUTION** (19:43:55, 80 s) lit on the
   guest only. They showed only in the grey "indicators differ" line. They were follow-on effects of red lines (2F).
3. **FCU AP1/AP2 lamps and HDG/VS dashes** lit on the guest for 36 s (from 19:34:35) with no button pressed. One FCU
   was unpowered at that moment, so the lamps were not compared. This is by design (power gating), and it cleared by itself.
4. **Ground:** EXT PWR AVAIL, GPU and cargo door open lamps differed from 19:40 to the end (grey only). These are each
   pilot's own ground services.
5. Brake pressure (grey, 142 s), fuel 4.4 kg apart (below the warning), seat positions (cosmetic, not compared).

Apart from the rudder trim position, no pilot control ended different without a red line.

## 5. Every red, by kind

71 red lines and 34 grey (quiet) lines.

| Kind | Lines | Why it was red |
|---|---|---|
| **Write limit (2A)** | **61** | flaps 11, speed brake 8, RUD TRIM RESET 7, mask cover 6, armrest 4, RUD TRIM 4, seats 4, tray table 3, cockpit door 2, jumpseat headrest 2, walkaround 10 |
| Elevator trim range (2B) | 2 | refused, then the out-of-range correction failed |
| Allowed values (2C) | 2 | cargo door selector AFT |
| Deny list, correct | 2 | CARGO DISC 1 and 2 "not mirrored" (cleared after 4 min) |
| Real differences, correct (2E, 2F) | 4 | F/O BARO 1, ECAM ENG lamp 2, ECAM STS lamp 1 |
| **False – nothing was wrong** | **0** | gate test 2: 100 (encoder check, lamp mid-burst, ECAM CLR as a toggle, correction loops) |

Of the 71, 19 were follow-ups of a line already open: 15 "correction did not take effect" and 4 "no ack".

The grey lines were MASTER CAUTION/WARNING lamps (12), ECAM CLR (6), ground lamps (9) and others (7: brake pressure,
battery volts, blower, elevator trim motor, F/O BARO inches, EXT PWR).

## 6. What the panel showed

- **Peak: 6 red controls at once** (19:46:50): elevator trim, F/O BARO, ECAM STS, flaps, RUD TRIM RESET, seat
  distance. Plus one amber "Outside the aircraft" line (6 cargo items). Gate test 2 peaked at 19 red cards.
- **At the end:** 5 red controls (elevator trim, F/O BARO, ECAM STS, flaps, RUD TRIM RESET) and the outside line.
- **Amber:** 29 controls gave a "not synced yet" line (standby instruments, door inside locks, EFB, refuelling panel,
  mask TEST, chart light), all expired. One "hardware fighting" line (CREW OXYGEN, 2D).
- **Grey:** "indicators differ" at most 14 at once.
- "Set both" was offered for flaps and speed brake and used 3 times. Because of 2A it could not work.

## 7. Fix plan

1. **P0 – bridge write registration (2A).**
   - Stop registering one definition per writable var. Use a small pool of write definitions, re-pointed for each
     write (clear and re-add), so the total stays far below the limit.
   - Log every SimConnect exception into the session log.
   - A definition error blocks that var for the session and shows red "Crosscheck cannot write X", never silent.
   - At connect, any definition error fails the preflight loudly.
   - Test: the adapter never registers more than a fixed number of definitions, whatever the profile size.
2. **P0 – confirm in the sim** (owner approved, after fix 1; announced before each run):
   - one supervised write of the tray table through the bridge, read back, then restored;
   - a registration-only check (no writes) that finds the exact definition limit.
3. **P1 – profile and server.**
   - `A_FC_ELEVATOR_TRIM` → `ignore` until the stream (CRO-13).
   - Fix the profile notes that call the stream CRO-17 (CRO-17 is the follower spike): the stream is CRO-13.
   - Cargo door selector AFT: add the seen value 1.
   - The server never sends a correction outside the profile's range or allowed values.
4. **P1 – rudder trim position (owner decision).** Compare `N_FC_RUDDER_TRIM_DECIMAL` as an **amber** indicator with
   a small tolerance: "Rudder trim differs – press RESET or adjust it". Never written. Pitch trim stays `ignore`
   until the stream (CRO-13).
5. **P2 – CREW OXYGEN fast toggling (2D).** Replay test from this log: no write of ours comes back as a pilot move.
6. **P2 – "Set both"** only for a var whose write has been confirmed in this session.
7. **Separate step – correct the earlier docs:** the write test conclusion in `positions-report-gate3.md` and gate
   test 2 cause H point to 2A.
8. **Gate test 4 checklist:** the 11 failed controls first, then everything section 3 lists as not used.

Input events stay out of scope. They are a fallback only if a control still fails after fix 1, and they would need
an owner decision, because they are a new kind of write.

### Verification for the fixes
- Unit test: definition count stays bounded with the full profile; a var at the end of the list is written through
  the pool.
- Unit test: a SimConnect definition error becomes a blocked var and a red line.
- Replay tests from this log: the flaps sequence at 19:37 (every write lands); the CREW OXYGEN stream (no echo);
  the elevator trim series (no command, no correction).
- In the sim (owner approved): the tray table write and the registration-only check.
- Gate test 4 with the checklist above.

## 8. Owner decisions (2026-11-03)
1. **Rudder trim position:** compare as an amber indicator with a small tolerance ("Rudder trim differs – press RESET
   or adjust it"). Pitch/elevator trim stays `ignore` until the stream.
2. **Sim checks after the write fix:** one supervised write of the tray table through the bridge, and the
   registration-only check for the exact limit. Each is announced before it runs.
3. **Earlier docs:** correct `positions-report-gate3.md` and gate test 2 cause H in a separate step.
4. **The stream is CRO-13** (CRO-17 is the follower spike). The profile notes get fixed.
