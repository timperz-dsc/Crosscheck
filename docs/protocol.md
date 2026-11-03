# Crosscheck protocol v5

How the bridges and the session server talk. The server is in [`server/`](../server/), the
JSON Schema for every message is in [`protocol/schema/`](../protocol/schema/) with one example
per message in [`protocol/examples/`](../protocol/examples/). The schema is the source of
truth; the C# bridge and the server validate against the same files.

The core rule is the same as everywhere in Crosscheck: every change is confirmed, and no
desync is ever silent. The server orders, relays, tracks acks, compares snapshots and raises
visible desyncs. It never invents a value and never writes into the pilot flying's (PF's) sim
what no pilot did. The one narrow exception is the safety release of a mirrored push button or
spring-loaded switch (section 5). Irreversible controls on the profile's deny list are never written
at all (section 5), and encoders are never corrected (section 7).

## 1. Transport

- One WebSocket per bridge: `ws://<host>:8787/ws` (phase 1: the server runs locally).
- Text frames, one JSON object per frame, max 256 KiB.
- `GET /health` answers `{"ok":true,"protocol":5,"sessions":<n>}`.

## 2. Envelope

```json
{ "v": 5, "type": "command", "session": "s_k3J9xQ2mPa", "seq": 42, "ts": 1793520000000, "body": { } }
```

| Field | Meaning |
|---|---|
| `v` | Protocol version, always `5` (bridge 0.5.0 and later; v4 was bridge 0.4.x, v3 0.3.x, v2 0.2.x, v1 the 0.1.0 zip). Any other version gets `error unsupported_version` in an envelope with the client's own `v`, so an old bridge can read it: "Your Crosscheck is out of date. Download the latest version." |
| `type` | Message type, snake_case (list below). |
| `session` | Session id; `null` in `create`, `join` and anything sent before a session exists. |
| `seq` | Server-assigned, only on server-to-client messages that belong to the session log. One counter per session, strictly increasing. A command's `seq` is its global order. Clients never send `seq`. |
| `ts` | Sender's clock, ms since the Unix epoch. For logs only, never used for ordering. |
| `replay` | `true` on messages the server re-sends after a resume. |
| `body` | The message. Unknown fields are rejected everywhere (envelope and body). |

Messages without `seq`: everything clients send, `resumed`, `ping`, `pong`, `stream`,
`error`, `join_rejected`. A client remembers the highest `seq` it has seen (`last_seq`) for
resume.

## 3. Message types

| Type | Direction | Purpose |
|---|---|---|
| `create` / `created` | C→S / S→C | Create a session; creator is host and PF. |
| `join` / `joined` / `join_rejected` | C→S / S→C | Join with the code; preflight check. |
| `preflight_check` / `preflight_result` | C→S / S→C | Compare versions before joining (takes no slot). |
| `peer_status` | S→C | Other pilot joined, lost, resumed, left, expired. |
| `leave` | C→S | Leave on purpose. |
| `resume` / `resumed` | C→S / S→C | Re-attach after a lost connection. |
| `command` | both | A pilot changed something. |
| `accepted` | S→C | Command logged; carries its seq. |
| `ack` | both | Result of applying a command or correction. |
| `desync` | S→C | Out of sync and not fixed. Stays open until `desync_cleared`. `tier` red or quiet. |
| `advisory` | S→C | An amber line: control not synced yet, hardware fighting, sim not connected (section 7a). |
| `notice` | S→C | `overridden`, `corrected`, `desync_cleared`, `stream_dropped`, `control_ack_missing`. |
| `snapshot_hash` | C→S | Hash of one panel group, every ~2 s. |
| `snapshot_request` / `snapshot_values` | S→C / C→S | Values of groups on mismatch or resync. |
| `snapshot_diff` | S→C | Differences of one group and the decision per var. |
| `drift` | S→C | Owned values (fuel, payload) that differ, for information. Rate limited. |
| `sim_state` | C→S | The bridge's simulator connected or disconnected. |
| `unsynced_input` | C→S | A pilot moved a control that is watched read-only (not synced yet). |
| `effect_report` | C→S | A push button's lamp once per burst, from the pilot who pressed and from the receiver (section 5). |
| `safety_release` | C→S | A receiver released an edge push button Crosscheck had pressed down, because the up never arrived (section 5). |
| `denied_input` | C→S | A pilot pressed a deny-list control (profile `never_write`) in their own cockpit; never mirrored (section 5). |
| `counter_suspect` | C→S | A switch behaves like a press counter; it is not synced any more (section 7a). |
| `write_blocked` | C→S | SimConnect refused to write a var in this bridge's sim; it is blocked there (section 5). |
| `correction` | S→C | Write this absolute target value (decided by the PF safety rule). Never for encoders or push buttons. |
| `control_request` | C→S | "I have control" (from the PM). |
| `control_give` | C→S | "You have control" (from the PF). |
| `control_denied` / `control_handover` / `control_changed` | S→C | Refused, freeze, new PF (with open differences). |
| `control_ack` | C→S | Client switched to its new role. |
| `stream` | both | Physics frame from the PF. |
| `ping` / `pong` | both | Heartbeat with RTT. |
| `error` | S→C | Message could not be processed. |

## 4. Sessions

**Create.** `create {preflight, name?}` → `created {session_id, join_code, client_id,
resume_token, seat: "host", role: "pf", pf_client_id, config}`. The creator's preflight is the
session's reference. `config` tells the client the server timings (heartbeat, lost timeout,
TTL, snapshot interval).

**Join code.** 6 characters from `23456789ABCDEFGHJKMNPQRSTUVWXYZ` (no 0/O, 1/I/L),
case-insensitive. The session id is separate and is what the envelope carries.

**Join.** `join {join_code, preflight, name?}` → `joined {…, seat, role: "pm", peer,
warnings}` and `peer_status {state: "joined", warnings}` to the other pilot. Max two pilots
(`join_rejected session_full`). A fresh join triggers a full snapshot comparison (section 7),
so a cold & dark cockpit is aligned to the PF the same way as after a resync.

**Preflight check before joining.** `preflight_check {join_code, preflight}` (no session) →
`preflight_result {join_code, reference, rejects, warnings}`, or `join_rejected`
(`unknown_code`, `session_full`). It takes no slot and changes nothing; the UI shows the table
and the pilot then sends `join`, which compares again.

**What must match.** Bridge major version, profile id and hash, aircraft package and app
version: a difference rejects the join. AIRAC differences warn. An aircraft version a bridge
could not read is sent as `unknown`; it cannot be compared, so it warns (on one or both
sides) instead of rejecting.

**Seats and roles.** Seat `host`/`guest` never changes. Role `pf` (holds the control token)
or `pm` changes with control transfer. The host is PF at the start.

**Preflight.** Sent in `create` and `join`:

```json
{ "bridge_version": "0.5.0", "profile_id": "fenix-a320", "profile_hash": "sha256:…",
  "aircraft": { "package_version": "3.0.0.7613", "app_version": "3.0.0.330" }, "airac": "2611" }
```

| Field | Rule |
|---|---|
| `bridge_version` | Major version must match (`0.x` vs `1.x` is rejected; minor/patch may differ). Before 1.0 the minor counts as the major: `0.4.x` vs `0.5.x` is rejected, `0.5.0` vs `0.5.3` is fine. |
| `profile_id`, `profile_hash` | Must match. The hash is compared as an opaque string. The bridge computes it as `sha256:` + hex SHA-256 of the profile file with CRLF normalised to LF, so line endings never change it. |
| `aircraft.package_version`, `aircraft.app_version` | Must match. |
| `airac` | Entered by hand (bridge `--airac`); Crosscheck does not read it from the aircraft. Mismatch, or unknown on one side → **warning**, not rejected, with the reminder "Check that both pilots use the same AIRAC". MCDU pages may differ. |

A rejection lists every difference (`diff: [{field, reference, joining, severity, message}]`),
warnings included, so the pilots see everything at once.

**Leave and expiry.** `leave` frees the slot at once. A lost client keeps its slot for 5
minutes (`session_ttl_ms`); then `peer_status expired`. The session ends when no pilot is left.

## 5. Commands, acks and desyncs

A pilot operates something in their own sim. Their bridge has already written it locally and
sends:

```json
{ "id": "h-118", "var": "S_OH_ELEC_BAT1", "class": "verified", "value": 1, "group": "overhead", "settle_ms": 150 }
```

| Class | Kind | Body | Rule |
|---|---|---|---|
| `verified`, `analog`, `owned` | absolute | `value` | Later seq wins (section 6). |
| `delta` | event | `delta` (never 0, never an absolute) | All deltas are applied, none dropped. |
| `pulse` | event | `value` (1; for an edge button 1 = down, 0 = up), `edge` and `gap_ms` for edge buttons | All presses and edges are applied, none dropped or merged. |

`stream`, `observe` and `ignore` are never commands (rejected by the schema). Range and value
limits come from the profile and are enforced by the bridge's guard. The bridge also sends them with
its `snapshot_values` (section 7), so the server never decides a correction outside them.

Flow:

1. Server assigns the next `seq`, appends the command to the session log, and sends
   `accepted {id}` (envelope `seq` = the command's seq) to the origin.
2. Server relays the command to the other pilot with `origin {client_id, seat, role}` and
   `attempt: 0`, same `seq`.
3. Receiver applies it, verifies (own L: var, then the effect source from the profile) and
   answers `ack {id, ok, observed, reason?}`. `reason` is only set with `ok: false`: `press_lost` (a
   mirrored press could not be applied: red `press_lost`), `not_synced` (a suspected counter, covered by
   its amber line: no red), `display_check` (an encoder click whose proof is its display: no red per
   click, see section 7) or `cannot_write` (SimConnect refused to write this var in the receiver's sim:
   red `cannot_write` at once, no re-send).
4. Server forwards the ack to the origin with `from` and `attempt`.

**Timeouts and retries.** The server waits `settle_ms + 300 ms + receiver RTT`. Only absolute
values (`verified`, `analog`, `owned`) are re-sent: no ack, or `ok: false`, → the same message
again with `attempt` 1, 2, 3, then `desync`. **Pulses, counters and delta steps are never
re-sent** (also not as corrections): a re-send would be another press or click. `ok: false`
→ desync at once; no ack → the server waits as long as the 3 re-sends would take (a burst of
key presses is applied one by one), then desync. The desync is `desync {var, expected, observed,
reason, tier: "red"}` to both pilots, `reason` `no_ack` or `refused` (the origin also gets the
last failed ack). Timers pause while the receiver is lost; on resume, commands sent before the
outage are delivered once (receivers dedupe by id, and never apply a pulse or delta id twice).
A late `ok` ack for a command that already failed clears its desync.

**Pulse effect check, once per burst (gate test 2 cause D).** A press whose var has an effect lamp
in the profile (`verify.lvar`) carries `effect {var, expect: same|parity}` on its down edge. Both
bridges read the lamp once per burst: 400 ms (or `settle_ms`, if longer) after the last edge of that
button, and send `effect_report {id, var, value}` for the burst's last down edge (the sender for the
last down it sent, the receiver for the last down it applied). `ok` in the ack only says the press was
stored. The server keeps one check per button: a newer down edge replaces it, every edge extends its
wait. It compares both reports: equal (`same`) or equal odd/even (`parity`) → nothing, also when
neither lamp changed (system unpowered on both sides). Different, or a report missing after 400 ms +
2 × 300 ms + RTTs after the last edge → red `desync effect_mismatch` with `values {pf, pm}` (the lamps).
A lamp is never judged in the middle of a burst.

**Tiers.** Every desync carries `tier`. `quiet` only for an indicator (class `observe`): it is
shown collapsed ("Indicators differ (n)"). Everything else is `red`: a pilot's input did not
arrive, or its result differs. A failed pilot input is never quiet. The display of an encoder is
reported under the encoder (class `delta`), so it is an input, not an indicator.
Test vectors: `protocol/test-vectors/desync-tiers.json`.

**Push button edges (CRO-29/30, profile `press: edge`).** Fenix push buttons count +1 on press and
+1 on release (odd = down, even = up; `profiles/fenix-a320/observe-report.md`). The bridge reads
them every sim frame and sends each down and up edge as its own pulse command:

```json
{ "id": "h-120", "var": "S_OH_FIRE_ENG1_TEST", "class": "pulse", "value": 1, "group": "overhead", "settle_ms": 100, "edge": "down", "gap_ms": 0 }
{ "id": "h-121", "var": "S_OH_FIRE_ENG1_TEST", "class": "pulse", "value": 0, "group": "overhead", "settle_ms": 100, "edge": "up", "gap_ms": 2840 }
```

- Sender: every step of the counter is one edge (+2 in one frame = down and up). A decrease
  (a binding that writes 0/1) is one edge if the parity changed (1 → 0 = up). A jump of more
  than 8 steps is not a click: at most one edge from the new parity. `gap_ms` is the time since
  the previous edge of that button.
- Sender, after a collision: Fenix adds 1 on every press and every release, so the local pilot's steps
  alternate press and release whatever the counter's parity. A click is always sent as down then up.
- Receiver: writes its own value + 1, never the sender's number. Edges of one button are applied
  strictly in order: each one first waits for the read-back of the previous edge this bridge wrote,
  and is judged against that written value, never against a stale read (gate test 2 cause D). A down
  is written only from up (even). A down that finds the button already down with no local press in
  progress is a lost press: ack `ok: false, reason: press_lost`, red for both pilots, never `ok`.
  An up is written only for a button Crosscheck itself pressed down, after that down was read back,
  and never sooner than max(50 ms, `gap_ms`) after it. A hold is mirrored for as long as it lasts;
  there is no fixed pulse timer.
- Local press in progress (gate test 2 cause C): while the local pilot is pressing a button (their down
  step seen, their up step not yet), incoming edges for that button are held back, in order, and applied
  after the local release (at most 10 s; after that they are judged as usual, so a down becomes a red
  lost press). Crosscheck's own mirrored down is then still released by the other pilot's up, also after
  complete local clicks (+2 each); never while the local pilot is pressing it.
- Only the down edge carries `effect` (the lamp is compared after the down edge).
- The server relays `edge` and `gap_ms` unchanged. Edges, like all pulses, are never re-sent.

**Safety release (the only write no pilot made).** If a receiver pressed a button down for a
mirrored "down" edge and the "up" never arrives, it releases the button itself:
- when: 30 s after the mirrored down (below Fenix's 40 s takeover latch), or at once when the
  session link is lost while the button is down;
- what: one "up" edge (own value + 1, to even) on that button only. Only for a button that
  Crosscheck itself pressed down in that sim; never a "down", never any other var;
- always with a red desync to both pilots, naming the button: the bridge shows it at once and
  sends `safety_release {var, group, cause: cap|link_lost|peer_lost, observed}`, after resume if
  the link was down; the server raises `desync safety_release` (red) to both. It clears when the
  button state matches again in a snapshot.
A button the local pilot is pressing is never released by Crosscheck; after complete local clicks
(+2 each) Crosscheck's own down may still be released. The other pilot being lost (`peer_status lost`)
counts like a lost link.

**Spring-loaded switches (profile `rest`, owner decision for gate test 2).** RUD TRIM and MAN V/S
CTL spring back to their middle position. They are `verified`: every change is a command with the
absolute value, so a hold is mirrored for as long as it lasts (the release is its own command back
to `rest`). The same safety release applies, with the same limits: if a receiver wrote a position
other than `rest` for the other pilot and the command back to `rest` never arrives (30 s cap, link
lost, other pilot lost), it writes the `rest` value itself, only for a switch Crosscheck itself
moved in that sim and only while it still shows the value Crosscheck wrote. Never another value,
never another var, never a switch the local pilot touched; always the red `safety_release` above
("Crosscheck returned RUD TRIM to neutral ...").

**Stuck pressed and "Release it" (gate test 2 cause C, owner decision 2026-11-02).** When two pilots
click one counter button at the same moment, Fenix can leave it resting pressed (odd) in one cockpit;
a click cannot fix it, because a click adds a press and a release. The server raises red
`stuck_pressed` when a button is held down on one side only for 5 s (`stuckMs`; `press_mismatch`
before that), with `client_id` = the pilot whose button is down and `values {pf, pm}` (parities). Only
that pilot's UI offers "Release it". It writes **one up step** (own value + 1, to even) in that pilot's
own sim, and only if no pilot holds it on either side: the other side is up (that is why the line
exists), the button did not move for 5 s here, and no mirrored edge is waiting for it. Never a down,
never automatic, never mirrored (the other cockpit's button is already up). It clears when the button
states match again in a snapshot.

**Deny list (profile `never_write`, owner decision 2026-11-02).** Irreversible controls: IDG 1/2
disconnect, ENG 1/2 and APU fire push buttons, every AGENT/DISCH button (engine, APU, cargo), RAT MAN ON,
passenger oxygen MAN ON. Not ENG MASTER, DITCHING or EVAC (reversible, they keep syncing); covers keep
syncing. The bridge's guard refuses these vars on every write path (command, correction, "Set both",
"Press here only", "Release it", safety release). They are watched instead: a press in one cockpit
sends `denied_input {var, group, label}` (at most one per 2 s), and the server raises red
`not_mirrored` for both pilots at once: "IDG 1 pressed in the host cockpit – not mirrored
(irreversible). Do the same in the other cockpit if intended." The raw value is also compared in
snapshots (class `denied`), so a pilot who joins later, or a press missed during a reconnect, gives the
same line after 2 s, naming the cockpit with the higher counter. Never a correction, never a write. It
clears when both sims show the same value (the same button pressed in the other cockpit). How to mirror
them is CRO-31.

**A var the sim refuses to write (gate test 3 cause A, CRO-40).** The bridge registers its SimConnect
data definitions in a bounded way (read chunks of 32 vars and a pool of 8 write definitions, at most 256 in
all) and traces every SimConnect exception to its call. If the sim still refuses a write (or the
definition it needs), the bridge blocks that var for as long as it runs, logs it with the var, and sends
`write_blocked {var, group, label}`; the server raises red `cannot_write` for both pilots ("Crosscheck
cannot write TRAY TABLE CAPT in the guest cockpit. Set it by hand in both cockpits."), blocking, so no
correction is sent for it. A command for it is acked `ok: false, reason: cannot_write` (the same red line,
no re-send). Every new session repeats `write_blocked` for each var still blocked. The line clears when
the cockpits match again; the next difference raises it again at once. A refused read or write-slot
definition at connect is a setup error: the bridge refuses to create or join a session and says so.

A command id that is already logged is not relayed again: the origin gets `accepted
{duplicate: true}` with the original seq (resend after reconnect).

## 6. Ordering and conflicts

Order is the server's `seq`. With TCP order per socket this gives one simple client rule:

- **Absolute classes:** skip an incoming command if you have an own command for the same var
  that is not yet `accepted`, or if you already processed a higher seq for that var. Ack it
  `{ok: true, superseded: true, observed: <current>}`. (If the server had given your own
  command a lower seq, its `accepted` would have reached you before the other command.)
- **Delta and pulse:** always apply; never skip.
- **Dedupe by id:** if a command id arrives again (re-send), do not apply it again; send the
  same ack again. This is what makes a re-sent delta safe.

Server side: when an absolute command arrives for a var that had an absolute command from the
other pilot within 1 s, the earlier origin gets `notice {kind: "overridden", id, by_role,
by_seat, by_seq}`. A newer absolute command also cancels pending re-sends of older commands
and corrections on that var, so a stale value is never re-sent.

## 7. Snapshots

**Hash.** Every ~2 s (`snapshot_interval_ms`) each client sends per panel group
`snapshot_hash {group, hash, applied_seq}`. The hash covers the vars of the group with a
snapshot class (`verified`, `analog`, `owned`, `delta`, `observe`, `pulse`, `toggle`, `mode`,
`denied`; never `ignore`). For a `delta` var the bridge reports the display value (e.g. `N_FCU_HEADING`)
under the encoder's name; with the profile's `compare_when` it adds `context`: `"unpowered"` (and value
`null`) while the unit is off, else the values of its mode vars (TRK/FPA, MACH, dashed display, the
selected radio of an RMP). A deny-list var is reported as `denied` with its raw value. An edge push button is reported as `pulse` with its parity only (1 = held
down), because its counter differs between the sims by design. Its lamp (`verify.lvar`) is the
button's state and is reported as `{var: <lamp>, class: "toggle", value, button: <button>}`
instead of an `observe` item. An indicator with a profile `alarm` (an input Crosscheck cannot sync
yet, e.g. the FCU managed lamps while the knob push is invisible) is reported as
`{var, class: "mode", value, message, report_as?}` instead of an `observe` item. The class does
not change the hash. Canonical form, so both bridges get the same hash:

1. One line per var: `NAME=VALUE\n`, sorted by name (ordinal).
2. Numbers: integers as is; other numbers with up to 6 decimals, trailing zeros removed,
   `-0` as `0`. Strings as JSON strings. Unknown as `null`.
3. SHA-256 over the UTF-8 text, first 16 hex characters.

`applied_seq` is the highest pilot-command seq the client has processed (logged, for
diagnosis). The server treats hashes as opaque.

**Loose indicators (CRO-34).** An `observe` var with a profile `tolerance` (battery volts ±0.5 V, brake
pressure ±0.3) or a `powered` flag (FCU and RMP lamps and flags) is not in the hash: values that wander
within their tolerance would make it flap. It travels with the hash instead, as
`snapshot_hash.loose: [{var, value, tolerance?, context?}]`, with `context: "unpowered"` and value
`null` while its unit is off. The server compares the loose lists of both pilots on every pair of hash
reports: over the tolerance (exact without one) for 10 s it raises `observe_mismatch` (quiet), within it
the line clears; a var unpowered on either side is not compared and its open line clears. An equal hash
never clears a loose line. An `observe` var with a profile `notice` (the rudder trim position, CRO-40) is
loose too and carries `amber: true` and `message` (the profile's text): over its tolerance for 10 s the
server raises the amber advisory `indicator_differs` (section 7a) with that text instead of a quiet desync,
and ends it when the values are within the tolerance again or the unit is off. A `toggle` or `mode` item whose lamp has a `powered` flag stays in the hash,
with value `null` and `context: "unpowered"` while the unit is off; it is not compared then (no line).

**Comparison.** The server compares the latest hash of both pilots for a group only when no
command or correction in that group is waiting for an ack, and both hashes were taken after
the last one settled. A mismatch must repeat 2 times in a row (effect vars lag ~120 ms). Then
it sends `snapshot_request {groups, reason}` to both; each answers one `snapshot_values
{group, values: [{var, class, value}]}` per group (`groups: null` = all groups). `verified` and `analog`
items carry the profile's limits: `values` (allowed values) or `range` ([min, max]). The server
diffs the values and sends `snapshot_diff` with a decision per differing var.

**Edge buttons are never corrected.** A `pulse` difference (held down on one side only) is a red
`press_mismatch`; a `toggle` difference (FD on here, off there) is a red `toggle_mismatch`,
raised under the button with the lamps in `values {pf, pm}`. An automatic press would be a write
no pilot made. Both are raised only after the difference was seen in 2 snapshot comparisons in a
row or for 2 s (`buttonHoldMs`), so a snapshot taken mid-mirror never raises a false red. The
cause can be upstream (FD goes dark with an unaligned IRS, a lamp is dark on an unpowered bus):
the UI lists a toggle item after the other red items of its group. A press in the cockpit would
be mirrored and flip both lamps, so the fix is the UI action "Press X here only": one click in
that pilot's own sim, not mirrored, a pilot action that is never automatic.

**Encoders are never corrected (gate test 2 causes E and F).** A `delta` display that differs is a
red `display_mismatch` with `values {pf, pm}`, raised only when it still differs 2 s
(`encoderHoldMs`) after the last click of that knob from either pilot, so a turn in progress never
raises a line, and an encoder click that did not land is no red line of its own (`display_check`).
The pilots fix it by turning the knob in the cockpit that is wrong. A display is not compared at all
(no line, logged `snapshot_not_compared`) while either side reports `context: "unpowered"` or the two
contexts differ. An encoder without a display is verified by read-back only; its failed click stays a
red `refused`.

**Mode items are inputs, never corrected.** A `mode` difference is a red `mode_mismatch`, never
quiet and never written, with the same 2 comparisons / 2 s rule. It is raised under `report_as`
(several indicators of one knob give one line) with the profile's text as `message`, e.g. "SPD
managed differs between the cockpits – push or pull the knob in the cockpit that is wrong."
Because the control itself is not synced, a pilot's fix in the cockpit stays local. It clears
when the values match again.

**PF safety rule.** Per differing var, the target is either a logged pilot command's value or
the PF's current value. Nothing else.

1. `observe` → `desync observe_mismatch` (tier quiet), but only after the var has differed for
   10 s (indicators lag their inputs). Outputs are never written.
2. Absolute class, the latest logged pilot command for the var has the value of one side,
   **and the PF never confirmed that command** (the PF neither sent it nor acked it ok) →
   correct the side that differs with that value (`source: command_log`, `source_seq`). Only
   this case may write into the PF's sim.
3. Otherwise → correct **only the PM** to the PF's value (`source: pf_reference`). Absolute
   classes only: encoders, push buttons, mode items and deny-list controls are never corrected.
4. The log value matches neither side, or a value is unknown → `desync` (`log_conflict`,
   `missing_value`); no correction.
5. A target outside the var's `values` or `range` (from `snapshot_values`) is never sent: red
   `out_of_range` instead, blocking, for the pilots to set by hand (CRO-40; gate test 3 saw a correction
   to 5400 for a knob with range [0, 1]).

Why rule 2 needs "never confirmed": if the PF applied a pilot's command and its aircraft later
changed the value itself, pushing the old command back would be a write no pilot did.

**Owned values** (fuel, payload; Fenix simulates them on each side). They are never written,
not even at join: each pilot loads fuel and payload in their own Fenix EFB, like in real flight
planning. An owned difference is never corrected, never a desync and never counted by the loop
guard. It is reported as `drift`: after each value comparison of a
group with owned vars the server sends `drift {group, values: [{var, pf, pm, delta}]}` to
both (unsequenced, not replayed; values in the dataref's own units, `delta` = pm − pf). Empty
`values` means no drift any more. Owned vars are not listed in `snapshot_diff`, and a group
whose only differences are drift does not count as differing for `control_changed`.
Drift is rate limited: a var that starts or stops drifting, or whose delta moved by more than
its `step` (sent by the bridge with the owned value in `snapshot_values`; profile `drift_step`,
default half of `drift_warn`: 50 kg for fuel), is sent at once; smaller changes at most every
10 s. Test vectors: `protocol/test-vectors/drift-rate.json`. The bridge shows one amber line
while a difference is larger than `drift_warn` ("Fuel differs by 400 kg – load the same fuel in
the EFB"), never a desync.

**Simulator not connected.** A bridge sends `sim_state {connected}` when its sim connects or
disconnects (and again after a resume). While a sim is disconnected the bridge answers no
`snapshot_request`, the server does not compare or request snapshots ("not comparable"), and
both pilots get one advisory `sim_disconnected` instead of a `missing_value` desync per var.
On reconnect the server requests all groups (`resync`).

`correction {id, var, class, target, group, settle_ms, source, source_seq?, request_id}` is
acked like a command (same retries). A confirmed correction → `notice corrected` to both. A
failed one → `desync correction_failed`.

**Correction loop guard.** If a var needs more than 3 corrections within 60 s (Fenix keeps
changing it back on one side), the 4th is not sent. Both pilots get `desync correction_loop`
and the var is not corrected again until the desync clears.

**No corrections during a control handover freeze.** Decisions in that window are reported
as `deferred`; right after `control_changed` the server requests those groups again and
decides with the new PF as reference.

**Open desyncs.** A desync stays open (listed in `resumed.desyncs`, `control_changed.desyncs` and `control_denied.desyncs`)
and is not repeated while it is open. It clears with `notice desync_cleared` when the values
match again (equal hashes or equal values), when a command for that var is acked `ok`, or when
a correction for it is confirmed. A new pilot command alone does **not** clear it: a click that
is refused too must never wipe the red line. A failed pulse is not in any snapshot, so only a
confirmed press clears it; an `effect_mismatch` also clears when the lamps match in a snapshot. While `correction_loop`, `correction_failed`,
`observe_mismatch`, `log_conflict` or `missing_value` is open, the var is not corrected
automatically (`held` in `snapshot_diff`); the same for `cannot_write` and `out_of_range`. `no_ack`/`refused` do not block corrections:
the snapshot is how those recover.

## 7a. Advisories

`advisory {key, kind, active, tier: "amber", seat, at, var?, label?, count?, message}` goes to
both pilots (sequenced, replayed). One line per `key`; the latest message wins; `active: false`
removes it. The active ones are listed in `joined.advisories` and `resumed.advisories`.

| Kind | Key | When | Ends |
|---|---|---|---|
| `not_synced` | `not_synced:<var>` | A pilot moved a control that is watched read-only (`unsynced_input`; the list is `profiles/<aircraft>/unsynced.csv`). `seat` = who moved it, `count` = how often. | 2 minutes after the last move (`notSyncedExpireMs`, CRO-34), or at the end of the session. |
| `counter_suspect` | `counter_suspect:<var>` | A bridge saw a `verified` var rise by exactly 1 three times in a row past its allowed values (`counter_suspect`): probably a press counter profiled as a switch (gate test 2 cause A). Both bridges stop syncing it and block its writes; the server never compares or corrects it again and drops its open desync. "Set both" is never offered for it, nor for any var whose values were seen outside its allowed set. | At the end of the session. |
| `hardware_fighting` | `hardware_fighting:<var>` | 3 `overridden` conflicts on one var within 30 s (both pilots' hardware set it). | 30 s without another override. |
| `sim_disconnected` | `sim_disconnected:<seat>` | That pilot's `sim_state {connected: false}`. | `sim_state {connected: true}`, or the pilot is lost or leaves. |
| `indicator_differs` | `indicator:<var>` | A loose indicator with a profile `notice` differs beyond its tolerance for 10 s (section 7). `message` = the profile's text ("Rudder trim differs – press RESET or adjust it."). | Within the tolerance again, or the unit is off on one side. |

Advisories are never desyncs and never write anything. The UI shows them below the red items.

**The panel (CRO-34).** The bridge turns open desyncs and advisories into one line per control
(`status.panel`, built by `PanelLines`): a button with its lamp, a knob with its display and mode flags,
an RMP's inner and outer knob, the walkaround items as one "outside" line. A line is red only when a
pilot can act and it has lasted: open 2 s, and 2 s after the last movement or press of that control (or
of a control upstream of it, profile `upstream`). Never red: lamps mid-burst, effects of another red
line, items outside the cockpit, a var the profile does not sync, and Crosscheck's own corrections
(`correction_failed`/`correction_loop` go to the log; while the values still differ, the control's own
line shows with its normal action). Each red line has one action ("Set both", "Press X here only",
"Release it") or a plain instruction. "Set both" is offered only for proven absolute switches and analog
knobs, and only once a write of that var changed the value and landed in this pilot's sim during this
session (CRO-40: gate test 3 offered it for flaps and speed brake, which Crosscheck could not write). Order: priority (1 safety, 2 flight-critical, 3 overhead
systems, 4 pedestal and radios, 5 lights, cabin and the rest; profile `group_priority`, `priority`),
then oldest first. "Not synced yet" counts only controls moved in the last 2 minutes.

## 8. Control token

One PF at a time. Control changes in real time, like in a real cockpit: "I have control" and
"You have control" take effect at once. Differences between the sims never block a transfer;
they are listed in `control_changed` so the new PF's UI can show them.

- **Taking control:** the PM sends `control_request {}`. Granted immediately.
- **Giving control:** the PF sends `control_give {}`. The PM gets control immediately, with
  the same handover sequence. It cannot be given to a pilot who is not connected.
- **PF lost:** if the PF is lost or left, a plain `control_request` is refused with
  `peer_lost`; the PM takes control by sending it again with `confirm: ["@peer_lost"]`.
- `control_denied {reason, desyncs, message}` with `reason` one of `peer_lost`,
  `already_pf` (take while PF), `not_pf` (give while PM), `handover_in_progress`.

Handover sequence (same for take and give):

1. `control_handover {epoch, kind: take|give, new_pf_client_id, new_pf_seat,
   previous_pf_client_id, freeze_ms: 300}` to both. All stream frames are dropped during the
   freeze, and no corrections are sent (section 7).
2. The token moves. `control_changed {epoch, kind, pf_client_id, pf_seat,
   previous_pf_client_id, confirmed, differences, desyncs, last_frame}` to both:
   - `confirmed`: `["@peer_lost"]` when taken from a lost PF, else empty.
   - `differences {groups, vars}`: groups whose latest snapshot hashes differ or that have a
     known differing var; vars from the last snapshot diff of those groups plus open desyncs.
     Informational only (for example fuel, which Fenix simulates on each side and always
     drifts a few kg).
   - `desyncs`: open desyncs at that moment.
   - `last_frame {frame, data, from}`: the last stream frame relayed from the previous PF, so
     the new PF has its last position; `null` if it never streamed.
3. Both answer `control_ack {epoch}`. A missing ack after 2 s → `notice control_ack_missing`
   to both.

The safety rules do not change with the token: corrections never go to the PF's sim without
a logged pilot command, and the correction loop guard stays.

**Takeover pushbutton.** The bridge treats a press of a sidestick takeover pushbutton (profile
`roles.takeover`, either seat) by the PM as `control_request` (granted at once, as above).
The press is never written into the sim of the pilot who is flying at that moment, because
there it would lock out the flying pilot's stick:
- PF presses it: mirrored to the PM's sim with the push button edge rule (section 5).
- PM presses it: the bridge holds the edges back and sends `control_request`. Once
  `control_changed` makes that pilot the PF, the held edges and any later ones are sent as a
  normal press from the new PF, so the AP disconnects in both sims. If control is not granted
  (`control_denied`, or no answer within 5 s), the edges are dropped and nothing is mirrored.
- Defence in depth: a bridge that is PF never writes a takeover press into its own sim; it acks
  such a command `ok: false` (red desync).

## 9. Stream

Only the current PF sends `stream {frame, data}` (position, attitude, velocity, sidestick;
the field set inside `data` is open until the follower spike). Up to 30 Hz; frames above
~36 per second are dropped. The server relays them to the PM with `from`, without `seq`, and
does not log frames (the log gets start/stop and counts). Stream from the PM is dropped, with
one `notice stream_dropped` every 5 s at most.

## 10. Liveness and resume

- Both sides send `ping {nonce, t, rtt_ms}` every 1 s; the other answers `pong {nonce, t}`
  at once. `rtt_ms` is the sender's last measured round trip.
- The server marks a client **lost** when it received nothing for 5 s (`no_messages`), or
  none of its pings were answered for 5 s (`no_pong`), or the socket closed. That catches
  stalls in either direction while the socket still looks open. The other pilot gets
  `peer_status lost`; the server closes the socket. Clients should apply the same 5 s rule to
  the server and reconnect.
- **Resume:** new socket, `resume {client_id, resume_token, last_seq}` with the session id in
  the envelope → `resumed {replayed, full_resync, desyncs, peer, …}`, followed by every logged
  message for that client with `seq > last_seq`, original seqs, `replay: true`. Commands it
  had received before the outage but not acked are re-sent (dedupe by id handles doubles).
- If those messages are no longer in the session log (ring buffer, 2000 entries), the
  server sends `resumed {full_resync: true}` and compares all groups (section 7) instead.
- An expired slot or wrong token → `error resume_failed`; join again.

## 11. Logging

One JSONL file per session (`server/logs/<date>-<time>-<session>.jsonl`): every message in
both directions except `stream`, `ping` and `pong`, plus server events (lost, resumed,
decisions, corrections, rejected joins). Heartbeats are summarised every 10 s (RTT per client,
stream counts, open desyncs) instead of logged one by one. Resume tokens are redacted. Attach
this file to a desync report.

## 12. Client obligations (C# bridge checklist)

1. Validate every message against `protocol/schema` before acting on it.
2. Keep `last_seq` (highest envelope seq seen) for resume.
3. Apply incoming commands with the rules of section 6 (superseded, dedupe by id, cache acks).
4. Verify before acking (own L: var, then the effect source after `settle_ms`); `ok` only
   when it matches. Never ack before writing.
5. Never write a value outside the profile's range or set, also not for a correction; ack
   such a correction `ok: false`.
6. Send `snapshot_hash` per group every `snapshot_interval_ms` using the canonical form;
   answer `snapshot_request` with one `snapshot_values` per group.
7. Follow `control_changed` (fly or follow) and answer `control_ack`. Stream only while PF.
   Send `control_request` when the pilot presses a takeover pushbutton while PM, and hold the
   press back until control is granted (section 8); show `control_changed.differences` to the
   new PF.
8. Ping every `heartbeat_ms`, answer pings, treat 5 s of silence as lost and resume.
9. If the server is gone, keep flying unsynced and say so; never pause, slew or "fix" the sim.

## 13. Defaults

| Setting | Value |
|---|---|
| Heartbeat / lost after / slot kept | 1 s / 5 s / 5 min |
| Ack timeout | `settle_ms` + 300 ms + RTT; 3 re-sends for absolute values only |
| Indicator hold (quiet desync) | 10 s |
| Edge button state differs (red) | 2 comparisons in a row or 2 s |
| Stuck pressed (red, "Release it") | 5 s held down on one side only |
| Encoder display differs (red, never corrected) | 2 s after the last click |
| Push button lamp check | once per burst, last edge + 400 ms |
| Incoming edges held back during a local press | until the local release, at most 10 s |
| Deny-list press shown | at most once per 2 s per button |
| Safety release of a mirrored button / up floor | 30 s / 50 ms |
| Takeover press waiting for control | 5 s, then dropped |
| Drift: step / at most | per dataref (fuel 50 kg) / every 10 s |
| Hardware fighting | 3 overrides within 30 s |
| Conflict window ("overridden") | 1 s |
| Snapshot interval / mismatch rounds | 2 s / 2 |
| Correction settle / loop guard (absolute values only) | 500 ms / more than 3 per 60 s |
| Control freeze / control ack timeout | 300 ms / 2 s |
| Session log ring buffer | 2000 entries |
| SimConnect definitions (bridge) | read chunks of 32 vars + 8 write slots, at most 256 |
| Stream | 30 Hz |

## 14. Not in v5

- Hosting, accounts, rate limits on join attempts (phase 1 runs locally).
- The stream field set (after the follower spike).
- Momentary pulses (`press: momentary`) in snapshots: they always return to 0.
