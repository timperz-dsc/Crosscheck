# Crosscheck protocol v1

How the bridges and the session server talk. The server is in [`server/`](../server/), the
JSON Schema for every message is in [`protocol/schema/`](../protocol/schema/) with one example
per message in [`protocol/examples/`](../protocol/examples/). The schema is the source of
truth; the C# bridge and the server validate against the same files.

The core rule is the same as everywhere in Crosscheck: every change is confirmed, and no
desync is ever silent. The server orders, relays, tracks acks, compares snapshots and raises
visible desyncs. It never invents a value and never writes into the pilot flying's (PF's) sim
what no pilot did.

## 1. Transport

- One WebSocket per bridge: `ws://<host>:8787/ws` (phase 1: the server runs locally).
- Text frames, one JSON object per frame, max 256 KiB.
- `GET /health` answers `{"ok":true,"protocol":1,"sessions":<n>}`.

## 2. Envelope

```json
{ "v": 1, "type": "command", "session": "s_k3J9xQ2mPa", "seq": 42, "ts": 1793520000000, "body": { } }
```

| Field | Meaning |
|---|---|
| `v` | Protocol version, always `1`. Other versions get `error unsupported_version`. |
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
| `desync` | S→C | Out of sync and not fixed. Stays open until `desync_cleared`. |
| `notice` | S→C | `overridden`, `corrected`, `desync_cleared`, `stream_dropped`, `control_ack_missing`. |
| `snapshot_hash` | C→S | Hash of one panel group, every ~2 s. |
| `snapshot_request` / `snapshot_values` | S→C / C→S | Values of groups on mismatch or resync. |
| `snapshot_diff` | S→C | Differences of one group and the decision per var. |
| `drift` | S→C | Owned values (fuel, payload) that differ, for information. |
| `correction` | S→C | Write this target value (decided by the PF safety rule). |
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
{ "bridge_version": "0.1.0", "profile_id": "fenix-a320", "profile_hash": "sha256:…",
  "aircraft": { "package_version": "3.0.0.7613", "app_version": "3.0.0.330" }, "airac": "2611" }
```

| Field | Rule |
|---|---|
| `bridge_version` | Major version must match (`0.x` vs `1.x` is rejected; minor/patch may differ). |
| `profile_id`, `profile_hash` | Must match. The hash is compared as an opaque string. |
| `aircraft.package_version`, `aircraft.app_version` | Must match. |
| `airac` | Mismatch, or unknown on one side → **warning**, not rejected. MCDU pages may differ. |

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
| `pulse` | event | `value` (usually 1) | All presses are applied, none dropped or merged. |

`stream`, `observe` and `ignore` are never commands (rejected by the schema). Range and value
limits come from the profile and are enforced by the bridge; the server checks structure only.

Flow:

1. Server assigns the next `seq`, appends the command to the session log, and sends
   `accepted {id}` (envelope `seq` = the command's seq) to the origin.
2. Server relays the command to the other pilot with `origin {client_id, seat, role}` and
   `attempt: 0`, same `seq`.
3. Receiver applies it, verifies (own L: var, then the effect source from the profile) and
   answers `ack {id, ok, observed}`.
4. Server forwards the ack to the origin with `from` and `attempt`.

**Timeouts and retries.** The server waits `settle_ms + 300 ms + receiver RTT`. No ack, or
`ok: false`, → the same message is re-sent with `attempt` 1, 2, 3. After the third re-send:
`desync {var, expected, observed, reason}` to both pilots, `reason` `no_ack` or `refused`
(the origin also gets the last failed ack). Retries pause while the receiver is lost; no
retries are used up then, the resume replays instead.

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
snapshot class (`verified`, `analog`, `owned`, `delta`, `observe`; never `pulse` or
`ignore`). For a `delta` var the bridge reports the display value (e.g. `N_FCU_HEADING`) under
the encoder's name. Canonical form, so both bridges get the same hash:

1. One line per var: `NAME=VALUE\n`, sorted by name (ordinal).
2. Numbers: integers as is; other numbers with up to 6 decimals, trailing zeros removed,
   `-0` as `0`. Strings as JSON strings. Unknown as `null`.
3. SHA-256 over the UTF-8 text, first 16 hex characters.

`applied_seq` is the highest pilot-command seq the client has processed (logged, for
diagnosis). The server treats hashes as opaque.

**Comparison.** The server compares the latest hash of both pilots for a group only when no
command or correction in that group is waiting for an ack, and both hashes were taken after
the last one settled. A mismatch must repeat 2 times in a row (effect vars lag ~120 ms). Then
it sends `snapshot_request {groups, reason}` to both; each answers one `snapshot_values
{group, values: [{var, class, value}]}` per group (`groups: null` = all groups). The server
diffs the values and sends `snapshot_diff` with a decision per differing var.

**PF safety rule.** Per differing var, the target is either a logged pilot command's value or
the PF's current value. Nothing else.

1. `observe` → `desync observe_mismatch`. Outputs are never written.
2. Absolute class, the latest logged pilot command for the var has the value of one side,
   **and the PF never confirmed that command** (the PF neither sent it nor acked it ok) →
   correct the side that differs with that value (`source: command_log`, `source_seq`). Only
   this case may write into the PF's sim.
3. Otherwise → correct **only the PM** to the PF's value (`source: pf_reference`). For
   `delta` the bridge turns the target display value into a corrective delta.
4. The log value matches neither side, or a value is unknown → `desync` (`log_conflict`,
   `missing_value`); no correction.

Why rule 2 needs "never confirmed": if the PF applied a pilot's command and its aircraft later
changed the value itself, pushing the old command back would be a write no pilot did.

**Owned values** (fuel, payload; Fenix simulates them on each side). They are never written,
not even at join: each pilot loads fuel and payload in their own Fenix EFB, like in real flight
planning. An owned difference is never corrected, never a desync and never counted by the loop
guard. It is reported as `drift`: after each value comparison of a
group with owned vars the server sends `drift {group, values: [{var, pf, pm, delta}]}` to
both (unsequenced, not replayed; values in the dataref's own units, `delta` = pm − pf). Empty
`values` means no drift any more. In `snapshot_diff` such a var has action `drift`, and a
group whose only differences are drift does not count as differing for `control_changed`.
The bridge shows drift as information; a profile can set `drift_warn` per dataref (fuel:
100 kg) for one amber line when the values differ by more at the start of the session.

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
match again (equal hashes or equal values), when a pilot sends a command for that var, or when
a correction for it is confirmed. While `correction_loop`, `correction_failed`,
`observe_mismatch`, `log_conflict` or `missing_value` is open, the var is not corrected
automatically (`held` in `snapshot_diff`). `no_ack`/`refused` do not block corrections:
the snapshot is how those recover.

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

**Takeover pushbutton.** The bridge treats a press of the sidestick takeover pushbutton on the
PM's own seat (profile `roles.takeover`) as `control_request`. The press itself is synced
like any other var, on both sides.

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
   Send `control_request` when the pilot presses the takeover pushbutton on their own seat
   while PM; show `control_changed.differences` to the new PF.
8. Ping every `heartbeat_ms`, answer pings, treat 5 s of silence as lost and resume.
9. If the server is gone, keep flying unsynced and say so; never pause, slew or "fix" the sim.

## 13. Defaults

| Setting | Value |
|---|---|
| Heartbeat / lost after / slot kept | 1 s / 5 s / 5 min |
| Ack timeout | `settle_ms` + 300 ms + RTT, 3 re-sends |
| Conflict window ("overridden") | 1 s |
| Snapshot interval / mismatch rounds | 2 s / 2 |
| Correction settle / loop guard | 500 ms / more than 3 per 60 s |
| Control freeze / control ack timeout | 300 ms / 2 s |
| Session log ring buffer | 2000 entries |
| Stream | 30 Hz |

## 14. Not in v1

- Hosting, accounts, rate limits on join attempts (phase 1 runs locally).
- The stream field set (after the follower spike).
- `pulse` vars with `press: counter` in snapshots (their effect is compared through observe vars).
