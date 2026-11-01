# bridge/

The per-pilot Windows bridge (C# / .NET 8, x64). It is the only part of Crosscheck that
touches the sim: it detects what the pilot does, sends it to the session server, applies the
other pilot's changes with two-step verification, answers snapshots and applies corrections.
Protocol: [docs/protocol.md](../docs/protocol.md).

| Project | What |
|---|---|
| `Crosscheck.Profiles` (+ `.Cli`, `.Tests`) | profile format, loader and validator |
| `Crosscheck.Bridge.Core` | sim and Fenix interfaces with fakes, sync engine, protocol client, local API, log |
| `Crosscheck.Bridge.SimConnect` | the real sim adapter (needs the MSFS 2024 SDK) |
| `Crosscheck.Bridge` | the `crosscheck` console program |
| `Crosscheck.Bridge.Tests` | unit and end-to-end tests (no sim needed) |
| `Crosscheck.Bridge.SimSmoke.Tests` | real-sim smoke test, skipped by default ([README](Crosscheck.Bridge.SimSmoke.Tests/README.md)) |

Requirements: Windows x64, .NET 8 SDK, the MSFS 2024 SDK with `MSFS2024_SDK` pointing at its
folder (for SimConnect), and Node 22+ for the session server.

## Build and test

```
dotnet build bridge/Crosscheck.sln
dotnet test bridge/Crosscheck.sln
```

The end-to-end tests start the real session server from `server/`, so run `npm install`
in `server/` once first. They take about a minute.

## Try it: two bridges with fake sims

Three terminals, all from the repo root.

1. Session server:
   ```
   cd server
   npm start
   ```
2. First pilot (creates the session and prints a join code):
   ```
   dotnet run --project bridge/Crosscheck.Bridge -- create --sim fake --name Sebastian
   ```
3. Second pilot (needs another local API port because both run on one PC):
   ```
   dotnet run --project bridge/Crosscheck.Bridge -- join <CODE> --sim fake --api-port 8792 --name Tim
   ```

In fake mode you can act as the pilot by typing into either bridge window:

| Command | Does |
|---|---|
| `set S_OH_ELEC_APU_MASTER 1` | flip a switch |
| `turn E_FCU_ALTITUDE 5` | turn an encoder 5 clicks |
| `press S_CDU1_KEY_FPLN` | press an MCDU key |
| `control` | ask for control (become PF) |
| `status` | print the status (same as the local API) |

Stop with Ctrl+C; the bridge leaves the session properly.

With the real sim, leave out `--sim fake` (MSFS 2024 with the Fenix A320 loaded). The bridge
finds the sim through SimConnect and keeps retrying if it is not running yet or exits.

## Options

```
crosscheck create [options]
crosscheck join <CODE> [options]

--server <url>        session server (default ws://127.0.0.1:8787/ws)
--profile <file>      aircraft profile (default profiles/fenix-a320/profile.yaml)
--sim simconnect|fake real sim (default) or an in-memory fake sim
--name <text>         your name shown to the other pilot
--api-port <n>        local API port on 127.0.0.1 (default 8790, -1 = off)
--log-dir <dir>       bridge log folder (default %LOCALAPPDATA%\Crosscheck\logs)
--gql-poll-hz <n>     Fenix GraphQL poll rate (default 2)
--airac <cycle>       AIRAC cycle of your nav data, e.g. 2611
--verbose             more console output
```

## Aircraft build in the preflight

The bridge reads the running aircraft build; it never copies it from the profile:

- app version: Fenix GraphQL `GetSoftwareVersion` (localhost:8083);
- package version: `fnx-aircraft-320/manifest.json`, found through `UserCfg.opt`. The file is
  only read.

It retries for up to 30 s while the aircraft loads. A value it cannot read is sent as
`unknown`, with a clear warning in the console, on `/status` and in the log. The profile's
`builds` list is only used to warn when the running version is not one the profile was made
for. In fake mode the package version is the profile's first build.

## Local API (for the coming UI)

Only on `127.0.0.1`; requests from web pages of other sites are refused.

- `GET http://127.0.0.1:8790/status`: session, join code, seat and role, server and sim
  connection, RTT, aircraft versions, warnings, open desyncs, counters.
- `ws://127.0.0.1:8790/events`: one JSON object per event (`desync`, `desync_cleared`,
  `overridden`, `corrected`, `peer_lost`, `peer_resumed`, `control_changed`, `sim_state`,
  `warning`, `correction_refused`, `safety_refused`, …).

## Log

One JSONL file per run in `%LOCALAPPDATA%\Crosscheck\logs` (or `--log-dir`), in the same line
format as the server's session log: every protocol message except ping, pong and stream,
plus events (echo suppressed, applied with verification result, refused writes, sim state).
Resume tokens are redacted. Attach it to a desync report together with the server log.

## Safety

Enforced in the sim adapter itself, so nothing above it can bypass it:

- Only profile vars are read or written; only writable classes; only values inside the
  profile's `values`/`range`. Anything else is refused and logged.
- There is no API for sim events at all: the bridge cannot pause, change sim rate, slew,
  move or freeze the aircraft. A test fails if such calls appear in the SimConnect adapter.
- The stream writer is only active on the pilot monitoring's side (and writes nothing yet,
  until CRO-17).
- As pilot flying, the bridge refuses every correction that is not from a logged pilot
  command.
- If the sim or the server goes away, the bridge keeps running and reconnects; both sims keep
  flying, just unsynced.
