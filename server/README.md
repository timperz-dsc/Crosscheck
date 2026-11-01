# server/

The Crosscheck session server: relays and verifies changes between the two pilots' bridges
(Node 22+ / TypeScript, Fastify + ws). Protocol: [docs/protocol.md](../docs/protocol.md),
schema: [protocol/schema/](../protocol/schema/).

## Run

```
cd server
npm install
npm start
```

The server listens on `ws://127.0.0.1:8787/ws` (health check: `http://127.0.0.1:8787/health`).
Stop it with Ctrl+C.

| Environment variable | Default | Meaning |
|---|---|---|
| `PORT` | – | Set by hosts such as Railway. If set, the server listens on it and on `0.0.0.0`. |
| `CROSSCHECK_PORT` | `8787` | Port when `PORT` is not set |
| `CROSSCHECK_HOST` | `127.0.0.1` (`0.0.0.0` if `PORT` is set) | Only this PC can connect. Set `0.0.0.0` so the second PC on the LAN can connect (two-computer test). |
| `LOG_DIR` | `logs` | One JSONL file per session; attach it to desync reports. (`CROSSCHECK_LOG_DIR` also works.) |
| `CROSSCHECK_CREATE_LIMIT` | `10` | Per IP: sessions that may be created per 10 min (`0` = off) |
| `CROSSCHECK_JOIN_LIMIT` | `30` | Per IP: join and preflight checks per 10 min (`0` = off) |
| `CROSSCHECK_INDICATOR_HOLD_MS` | `10000` | How long an indicator must differ before it is shown (tests shorten it) |
| `CROSSCHECK_TRUST_PROXY` | on at Railway | Read the client IP from `X-Forwarded-For` (`1`/`0`) |

Every session event is also printed to stdout as one JSON line (join codes left out).
Hosting on Railway: [docs/hosting.md](../docs/hosting.md). `/health` returns the server version.

PowerShell example for the LAN test: `$env:CROSSCHECK_HOST = "0.0.0.0"; npm start`
(Windows may ask to allow Node through the firewall).

## Test

```
npm test          # all tests, no sim needed
npm run typecheck
```

Tests start a real server and two fake bridges (`test/fake/`) that simulate a sim in memory:
latency, lost commands, dropped acks, values that refuse to change, Fenix logic that changes
values back, one-way stalls and disconnects. All timings run 20× faster than real (1 s
heartbeat → 50 ms), so the full suite takes about half a minute.

| Test | What it proves |
|---|---|
| 01 | Command → ack, in seq order; MCDU key presses all arrive |
| 02 | Lost command → re-sent → ok; a re-sent delta is applied once |
| 03 | Never acked → desync after 3 re-sends; refused value → desync |
| 04 | Both pilots set one var at once → later seq wins on both sims, "overridden" notice |
| 05 | Concurrent encoder deltas → all applied, same sum on both sides |
| 06 | 10 s disconnect → resume → missed commands replayed; log overwritten → full resync; slot expiry |
| 07 | One-way stalls (socket open) in both directions → detected as lost |
| 08 | Snapshot mismatch → corrections follow the PF safety rule |
| 09 | Control granted immediately (differences listed, never blocking); PF hands over; lost PF needs a confirm |
| 10 | Preflight rejects profile/build/major-version mismatches, warns on AIRAC and unknown versions; check before join |
| 11 | Correction loop guard: stops after 3 corrections in 60 s, clears correctly |
| 12 | No corrections during a control handover freeze |
| 13 | Only the PF streams; rate limit; stream frozen during handover |
| 14 | Owned values (fuel) are never written, not even at join; differences are reported as drift only |
| 15 | `/health` with version; per-IP limit on create and join; one stdout line per event; an unwritable log folder never stops a session |
| 16 | An input desync stays until an ok ack or a match: more refused clicks never wipe it; failed key presses are not cleared by snapshots; a late ok ack clears it |
| 17 | Tiers: an indicator is quiet and only after 10 s; a failed correction is red |
| 18 | Advisories: sim not connected (one line, nothing compared, resync after), not synced yet (both pilots, kept on resume), hardware fighting (3 overrides in 30 s) |
| 19 | Owned drift rate limit: steady difference sent once, steps larger than 50 kg at once, small changes at most every 10 s |
| 20 | Key effect compared between the sims (same, parity, both unpowered is fine, missing report is red); pulses never re-sent, absolute values 3 times |
| 21 | Replay of gate test 1 (`test/fixtures/gate-test-1.json`): ELEC PUMP counter 1…28 against [0, 1] stays red to the end; host sim disconnect gives no missing_value desyncs |
| unit, schema | Safety rule, loop guard, preflight, join codes, replay log, schema examples, tier and drift-rate vectors, gate test 1 fuel drift |

## Layout

- `src/index.ts` – start; `src/app.ts` – HTTP + WebSocket; `src/config.ts` – all timings
- `src/protocol/` – message types and schema validation (loads `../protocol/schema`)
- `src/session/` – sessions, acks and retries, snapshots and the PF safety rule
  (`corrections.ts`), loop guard, control token, preflight, join codes, replay log
- `src/log/` – JSONL session log
- `test/` – vitest suites and the fake bridge
