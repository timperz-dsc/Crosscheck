# Observe report – Fenix push buttons (CRO-29 part 1)

Read-only observation, 2026-11-02. Fenix A320 IAE (3.0.0.7613) at the gate, external power, IRS aligned.
The owner clicked each button: once, twice as fast as possible, and a 2 s hold. `profile-probe observe`
logged every change of every watched var each sim frame. Nothing was written to the sim.

- Data: `observe-results.csv` (full run), `observe-results-clock_chr.csv` (CHR redo),
  `observe-results-fd1.csv` (an earlier FD-only run). Summaries per step are in `.probe/observe-summary*.txt`.
- About 58 frames per second (5 frames ≈ 85 ms). 34,526 frames; the longest gap between two frames was 278 ms.
- **Excluded:**
  - Captain takeover (steps 58–60): a hardware button sent faulty signals. The FO takeover (mouse) is the
    reference.
  - CLOCK_CHR in the full run (steps 28–30): the glareshield CHRONO was clicked by mistake. The redo is used instead.

## Result in one line

The owner's reading is confirmed. Every Fenix S_ push button counts **+1 on press and +1 on release**: odd means
down, even means up, and a hold stays odd. All 172 recorded edges of the valid buttons (three runs) were exactly +1. None went
back to 0, none skipped a value, and no click was lost. Every lamp follows the **down** edge.

## Per button

| Button | Var | Click | Fast double click | 2 s hold | Effect |
|---|---|---|---|---|---|
| MCDU1 S, E, 1, LSK1L, CLR | `S_CDU1_KEY_*` | +1 / +1 | 4 edges, all seen; fastest 51 ms (3 frames) apart | odd for 1.8–2.5 s, then even; no auto-repeat (one character per hold) | MCDU1 screen 25–117 ms after down (GraphQL push) |
| ECAM ENG | `S_ECAM_ENGINE` | +1 / +1 | both presses toggled | odd, then even | `I_ECAM_ENGINE` toggles on down, 49–84 ms |
| ECAM ALL | `S_ECAM_ALL` | +1 / +1 | each press steps the page (ENG lamp on, then off) | odd, then even | no lamp of its own |
| ECAM EMER CANC | `S_ECAM_EMER_CANCEL` | +1 / +1 | as usual | as usual | `I_ECAM_EMER_CANCEL` no change (nothing to cancel at the gate) |
| ECAM CLR | `S_ECAM_CLR_LEFT` | +1 / +1 | as usual | as usual | `I_ECAM_CLR_LEFT` no change; it lit during the fire test, so it follows ECAM, not the button |
| Clock CHR (redo) | `S_MIP_CLOCK_CHR` | +1 / +1 | as usual | as usual | chrono starts/stops on down (`N_MIP_CLOCK_CHRONO` −1 → 0 after 96 ms, then counts seconds) |
| Clock RST | `S_MIP_CLOCK_RST` | +1 / +1 | as usual | as usual | none here: the chrono was off (−1) |
| XPDR CLR | `S_PED_ATC_CLR` | +1 / +1 | as usual | as usual | no effect var |
| FD (EFIS 1) | `S_FCU_EFIS1_FD` | +1 / +1 (was already at 70/78 from earlier use) | both presses toggled; the second toggle shows 280–360 ms after its down | odd, then even | `I_FCU_EFIS1_FD` toggles on down, 50–71 ms; `I_FCU_EFIS2_FD` unchanged |
| AP1 | `S_FCU_AP1` | +1 / +1 | both toggled | odd, then even | `I_FCU_AP1` toggles on down, 51–70 ms |
| EFIS CSTR | `S_FCU_EFIS1_CSTR` | +1 / +1 | both toggled | odd, then even | `I_FCU_EFIS1_CSTR` toggles on down, 55–107 ms |
| VOICE (ASP 1) | `S_ASP_VOICE` | +1 / +1 | both toggled | odd, then even | `I_ASP_VOICE` toggles on down, 68–102 ms |
| YELLOW ELEC PUMP | `S_OH_HYD_YELLOW_ELEC_PUMP` | +1 / +1 | both toggled | odd, then even | `_L` lamp toggles on down, 60–103 ms; `_U` (FAULT) blinks once when the pump starts |
| ENG 1 FIRE TEST | `S_OH_FIRE_ENG1_TEST` | +1 / +1, **no effect** | **no effect** | odd for 2.8 s | fire button lamp after **1.05 s** held, ECAM CLR after 1.7 s, MASTER WARN flashing from 2.0 s; all off 76–500 ms after up |
| SVCE INT OVRD | `S_OH_SVCE_INT_OVRD` | +1 / +1 | as usual | odd for 2.6 s | **momentary**: `I_OH_SVCE_INT_OVRD` on only while held (on 68–87 ms after down, off 52–116 ms after up) |
| FO takeover | `S_FC_FO_INST_DISCONNECT` | +1 / +1, no lamp | as usual | odd for 2.8 s | `I_FC_SIDESTICK_PRIORITY_CAPT_ARROW` (captain stick locked out) on after **2.1 s** held, off 111 ms after up |

## The four questions

1. **Back to 0 or counting?** Always counting: +1 per press, +1 per release. No button ever went back to 0.
2. **Two clicks very fast or inside one frame?**
   - Every click was seen as two separate edges.
   - The fastest gap between two edges was 51 ms (3 frames); the shortest press was 66 ms (4 frames).
   - A +2 jump inside one frame never happened. At 58 fps it would need two edges less than 17 ms apart, which is
     only realistic during a stutter (one 278 ms frame gap was seen) or with a key macro. The rule must still
     handle it.
3. **Hold?** The value is odd for the whole hold, with no other changes, and becomes even on release.
4. **Lamps?**
   - Toggle buttons switch their lamp on the down edge, 50–110 ms (3–6 frames) later. Release does nothing to them.
   - Hold buttons (SVCE INT OVRD, FIRE TEST, takeover) show their effect only while the value is odd. Some need a
     minimum hold time: FIRE TEST about 1 s, the takeover arrow about 2 s.

Three findings that change the profile:
- **FIRE TEST** does nothing on a short click. The 150 ms pulse of gate test 1 could never have run the test on the
  other sim (cause D confirmed).
- **SVCE INT OVRD is momentary**, not a latch. The profile entry `verified [0, 1]` (owner decision 3) is wrong; it
  belongs with the hold buttons.
- **AP1, CSTR and VOICE** behave like every other counter button and can leave the review list as pulse buttons with
  the same rule.

## Proposed rule for CRO-29 and CRO-30: mirror edges

One rule for every Fenix S_ push button, held or not. It replaces `press: counter`, the planned `press: hold` and
`pulse_ms`.

**Sender**
- Watch every push button each frame. A change the bridge did not write itself becomes edges:
  - **+1** is one edge: down if the new value is odd, up if it is even.
  - **+2 or more in one frame** is one edge per step, in order. For example, 4 → 6 is down, then up.
  - **A decrease** (a hardware binding that writes 0/1): if the parity changes, it is one edge from the new
    parity (1 → 0 is up). If the parity stays the same (2 → 0), there is no edge, and it is logged.
- Each edge is sent as its own command: `var`, `edge: down|up`, `seq`, plus the time since the last edge of that var.
- The old "0 → non-zero" check goes away. That alone fixes cause B (every MCDU key, ECAM key, CLR …).

**Receiver**
- **Down:** if its own value is even, write own + 1. If it is already odd, the local pilot is holding the same
  button. Then nothing is written and the ack is `ok` ("already down").
- **Up:** if its own value is odd, write own + 1; if it is even, nothing.
- **Always relative, never the sender's absolute value.** The two counters drift apart (FD read 70 and 78 here),
  which is fine, because Fenix only reacts to the steps.
- **Order and timing:**
  - Apply edges in `seq` order.
  - An up is written only after the down has been read back as odd. It is also never earlier than the gap the
    sender saw, with a floor of 50 ms: the shortest gap a real click produced here, which Fenix handled every time.
  - This floor is not a pulse timer: a 2 s hold is mirrored as a 2 s hold.
- **Verify:**
  1. Read back the own var (parity) within 1–2 frames.
  2. Then the effect, where one is known. For a toggle lamp, it must change within 400 ms of the down edge
     (seen: 50–110 ms, 360 ms worst case for a fast FD double click). For a hold lamp, it must be on while down
     and off after up. For an MCDU key, the screen must change within about 500 ms.
  3. If either check fails, the ack is `ok: false` and a red desync is raised.

**Safety cap**
- If no up arrives 30 s after a mirrored down, the receiver writes the up itself and raises a red desync ("FIRE TEST
  released by the safety cap").
- If the session link drops while a mirrored button is down, the receiver releases it at once, with the same red
  desync.
- 30 s is below the 40 s takeover latch.
- **Owner decision (2026-11-02): approved as a narrow exception.** Crosscheck may only write an "up" edge to release
  a button that Crosscheck itself pressed down in that sim, when the "up" never arrived (30 s cap or link lost).
  Never a "down", never any other var, always a red desync. A button the local pilot is holding is never released.
  Written into the hard safety rules in `CLAUDE.md` and into `docs/protocol.md` section 5.

**Also**
- Snapshots must not compare the raw S_ counters, which differ by design. Compare only their parity: both up at
  rest, or a desync.
- MASTER WARNING/CAUTION already count press and release (the special case in `CLAUDE.md`). They fall under the same
  rule and stop being special.
- 0/1 tolerance: a binding that writes only 0 and 1 gives 0 → 1 = down and 1 → 0 = up, which the parity rule
  already handles. The excluded captain hardware data shows exactly this pattern (2 → 1 → 0, 0 → 1 → 0 → 1 → 0).

## Takeover buttons: sync them, or only trigger the control transfer?

**Owner decision (2026-11-02):** a takeover press is never written into the sim of the pilot who is flying at that
moment. A PM's press requests control; once control is granted, the press is mirrored as a normal press from the
new PF.

Why:
- Only the PF's sim flies, and the PM's sidestick input never reaches it.
- The data shows that holding the FO takeover locks out the captain stick in that sim (the arrow appears after
  about 2 s).
- If a PM's press were mirrored into the PF's sim, the PF's own stick would stop working for as long as the button
  is held, and nobody's stick input would replace it. A 40 s hold would latch that for good.

How:
- **PF presses it:** mirrored to the PM's sim with the edge rule. The PM's sim only follows the stream, so this is
  harmless, and it keeps the AP disconnect, arrows and aural the same on both sides.
- **PM presses it:**
  1. The bridge holds the edges back and sends `control_request`, which is granted at once, as today.
  2. Once `control_changed` makes that pilot the PF, the held edges (and any later ones) are sent as a normal press
     from the new PF. The other sim is now the PM's, so the AP disconnects in both sims and nothing locks out the
     flying stick.
  3. If control is not granted (`control_denied`), the edges are dropped and nothing is mirrored.
- Written into `docs/protocol.md` section 8 (takeover pushbutton).

## Next steps

1. CRO-29/30: implement the edge rule in the bridge (sender, receiver, verify, safety cap, parity snapshot) and
   replay this CSV in the bridge tests.
2. Profile: move SVCE INT OVRD, FIRE TEST and the takeover buttons into the edge rule. Move AP1, CSTR and VOICE off
   the review list.
3. Protocol: add the desync reason `safety_release` to the schema (with a protocol version step) when the edge rule
   is implemented.
