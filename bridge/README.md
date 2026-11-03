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
| `Crosscheck.Bridge` | the `crosscheck` program: app window (WebView2), tray icon, console |
| `Crosscheck.Bridge.Tests` | unit and end-to-end tests (no sim needed) |
| `Crosscheck.Bridge.SimSmoke.Tests` | real-sim smoke test, skipped by default ([README](Crosscheck.Bridge.SimSmoke.Tests/README.md)) |

Requirements: Windows x64, .NET 8 SDK, and Node 22+ for the session server and the UI. SimConnect comes
from the MSFS 2024 SDK if `MSFS2024_SDK` points at its folder, otherwise from the copies in
`third_party/simconnect/` (Microsoft's files, see the README there).

## Build and test

```
cd ui && npm install && npm run build && cd ..   # the UI; the bridge build copies ui/dist to wwwroot
dotnet build bridge/Crosscheck.sln
dotnet test bridge/Crosscheck.sln
```

The end-to-end tests start the real session server from `server/`, so run `npm install`
in `server/` once first. They take about a minute.

## Run it

`crosscheck` without a command starts the bridge and opens the Crosscheck window: the UI in
WebView2 (part of Windows 10/11). Closing the window hides it to the tray; the tray icon offers
"Open Crosscheck" and "Quit" (Quit leaves the session properly). Links to outside sites open in
the default browser. Without the WebView2 runtime the UI opens in the default browser instead,
with a warning in the console. The same UI also works in any browser on this computer at
`http://127.0.0.1:8790/` (for debugging). Create or join the session in the window.

## Try it: two bridges with fake sims

Three terminals, all from the repo root.

1. Session server:
   ```
   cd server
   npm start
   ```
2. First pilot (the Crosscheck window opens; click Create session):
   ```
   dotnet run --project bridge/Crosscheck.Bridge -- --sim fake --name Sebastian
   ```
3. Second pilot (needs another port because both run on one PC; enter the code under Join):
   ```
   dotnet run --project bridge/Crosscheck.Bridge -- --sim fake --api-port 8792 --name Tim
   ```

Headless, without the UI: `crosscheck create ...` prints the join code, and
`crosscheck join <CODE> ...` joins right away.

In fake mode you can act as the pilot by typing into either bridge window:

| Command | Does |
|---|---|
| `set S_OH_ELEC_APU_MASTER 1` | flip a switch |
| `turn E_FCU_ALTITUDE 5` | turn an encoder 5 clicks |
| `press S_CDU1_KEY_FPLN` | press an MCDU key |
| `control` | "I have control": take control at once (as PM) |
| `control @peer_lost` | take control although the PF is not connected |
| `give` | "You have control": hand control to the other pilot (as PF) |
| `status` | print the status (same as the local API) |

Stop with Ctrl+C; the bridge leaves the session properly.

With the real sim, leave out `--sim fake` (MSFS 2024 with the Fenix A320 loaded). The bridge
finds the sim through SimConnect and keeps retrying if it is not running yet or exits.

## Options

```
crosscheck [options]              UI mode (browser + tray)
crosscheck create [options]       headless: create a session
crosscheck join <CODE> [options]  headless: join a session

--server <url>        session server (default ws://127.0.0.1:8787/ws)
--profile <file>      aircraft profile (default profiles/fenix-a320/profile.yaml)
--sim simconnect|fake real sim (default) or an in-memory fake sim
--name <text>         your name shown to the other pilot
--api-port <n>        local API and UI port on 127.0.0.1 (default 8790, -1 = off)
--log-dir <dir>       bridge log folder (default %LOCALAPPDATA%\CrosscheckData\logs)
--gql-poll-hz <n>     Fenix GraphQL poll rate (default 2)
--airac <cycle>       AIRAC cycle you loaded, typed by hand (not read from the aircraft), e.g. 2611
--seat-side <side>    your cockpit seat: captain or fo (default: host captain, guest fo)
--ui-dir <dir>        folder with the built UI (default wwwroot next to the program)
--no-browser          no window and no browser on start (tests; the UI stays reachable by URL)
--no-tray             no tray icon (closing the window then quits)
--verbose             more console output
```

## Control

Control changes at once, like in a real cockpit. Differences between the sims never block
it; they are shown to the new PF in the `control_changed` event ("Open differences: ...").

- Take control: "I have control" in the UI, `control` in the console, or press the **sidestick takeover pushbutton on
  your own seat** while you are PM (profile `roles.takeover`; your seat is `--seat-side`).
  The press is also synced to the other sim like any other button.
- Give control: "You have control" in the UI, or `give` in the console (as PF).
- If the PF is not connected, a plain request is refused; `control @peer_lost` takes it anyway.

## Aircraft build in the preflight

The bridge reads the running aircraft build; it never copies it from the profile:

- app version: Fenix GraphQL `GetSoftwareVersion` (localhost:8083);
- package version: `fnx-aircraft-320/manifest.json`, found through `UserCfg.opt`. The file is
  only read.

It retries for up to 30 s while the aircraft loads. A value it cannot read is sent as
`unknown`, with a clear warning in the console, in the UI and in the log. The other side sees
it as a warning in the preflight table; the join is allowed. The profile's
`builds` list is only used to warn when the running version is not one the profile was made
for. In fake mode the package version is the profile's first build.

## Local API

Serves the UI and the API on `127.0.0.1` only. Refused: requests from web pages of other
sites (Origin), a foreign Host header (DNS rebinding), and POSTs that are not JSON.

| Request | Does |
|---|---|
| `GET /api/status` | everything the UI shows: `phase` (idle, preflight, waiting, session, ended), this computer, preflight table, crew and roles, panels with open differences, fuel drift, open desyncs with both values and "Set both" options, server and sim connection, counters |
| `GET /api/recent` | the last 50 entries of the Recent list, newest first |
| `WS /api/events` | one JSON object per event (`action_confirmed`, `action_applied`, `desync`, `desync_cleared`, `corrected`, `peer_lost`, `control_changed`, `control_denied`, `takeover_pressed`, `warning`, `safety_refused`, …) |
| `POST /api/session/create` | create a session |
| `POST /api/session/check {code}` | compare versions with the session before joining (`preflight_check`) |
| `POST /api/session/join {code}` / `cancel` / `leave` | join, drop the preflight table, leave |
| `POST /api/control/take {confirm_peer_lost?}` / `give` | "I have control" / "You have control" |
| `POST /api/set {var, value}` | "Set both to X": a normal pilot command from this seat, logged as such; only switches and knobs, only values the profile allows |

Errors answer 409 with `{ok: false, error}`; the error is a sentence the UI shows as is.

## Log

One JSONL file per run in `%LOCALAPPDATA%\CrosscheckData\logs` (or `--log-dir`), in the same line
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
  until the stream, CRO-13; its field set comes from the follower spike, CRO-17).
- As pilot flying, the bridge refuses every correction that is not from a logged pilot
  command.
- If the sim or the server goes away, the bridge keeps running and reconnects; both sims keep
  flying, just unsynced.

## Installer, updates and the console

- `crosscheck.exe` is a Windows program without a console window. Started from a terminal with
  arguments (`create`, `join`, `--sim fake` ...) it attaches to that terminal, so output and
  Ctrl+C work. A Windows program does not make the shell wait: in PowerShell use
  `Start-Process -Wait -NoNewWindow .\crosscheck.exe create` (or pipe it: `.\crosscheck.exe create | Out-Host`).
- Releases are made with Velopack (installer `Crosscheck-win-Setup.exe`, per user, self-updating from
  GitHub releases). See [docs/release.md](../docs/release.md). Build one locally with `bridge/package.ps1`.
- Updates install only when the pilot clicks "Restart and update" or at the next start, never during a
  session. A copy that is not installed (a build folder) never updates itself.
- Settings, logs and the WebView2 profile are in `%LOCALAPPDATA%\CrosscheckData`. Older versions used
  `%LOCALAPPDATA%\Crosscheck`, which is now the installer's folder; the first start moves the data over.
