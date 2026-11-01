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
| `CROSSCHECK_PORT` | `8787` | Port |
| `CROSSCHECK_HOST` | `127.0.0.1` | Only this PC can connect. Set `0.0.0.0` so the second PC on the LAN can connect (two-computer test). |
| `CROSSCHECK_LOG_DIR` | `logs` | One JSONL file per session; attach it to desync reports. |

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
| 14 | Owned values (fuel) aligned once at join, then reported as drift only |
| unit, schema | Safety rule, loop guard, preflight, join codes, replay log, schema examples |

## Layout

- `src/index.ts` – start; `src/app.ts` – HTTP + WebSocket; `src/config.ts` – all timings
- `src/protocol/` – message types and schema validation (loads `../protocol/schema`)
- `src/session/` – sessions, acks and retries, snapshots and the PF safety rule
  (`corrections.ts`), loop guard, control token, preflight, join codes, replay log
- `src/log/` – JSONL session log
- `test/` – vitest suites and the fake bridge
