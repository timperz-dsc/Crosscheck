# What we learned from YourControls

[YourControls](https://github.com/YourControls/yourcontrols) made shared cockpit possible in
Microsoft Flight Simulator. Its authors (Sequal32 and the maintainers and contributors who
followed) and its community built and kept alive, for six years, something many thought
could not be done, and the project is still the reference for everyone who flies together in
MSFS. Crosscheck builds on the approach YourControls proved works: mirror the cockpit on
both PCs, let one pilot fly, hand over control. This document records what we learned from
its code, issues and changelog, so that we start from their experience.

Research for CRO-16. Written 2026-11-01. YourControls is GPL-3.0 and Crosscheck is MIT, so no
code from it is copied or translated here; everything is described in our own words and
linked.

## 0. Sources and limits

- Code: [Sequal32/yourcontrols](https://github.com/Sequal32/yourcontrols) (622 commits, last
  commit 2026-04-05). The new home [YourControls/yourcontrols](https://github.com/YourControls/yourcontrols)
  is a fresh import: 4 commits, no issues, only dependency-bot PRs. Version 2.8.15/16 says
  "main repo moved". All real history and all issues are still in the old repo.
- Issues: the old repo has 233 issues (53 still open) and 82 PRs (42 are dependency bumps).
  About 83 issues are just "please add aircraft X". That leaves roughly 150 problem reports.
- I read every issue title and the first ~1500 characters of every body, plus the comment
  threads of about 40 of the most relevant issues. The `gh` CLI is not installed on this PC
  and the unauthenticated GitHub API allows only 60 calls per hour, so I did not read every
  comment of every issue. Group counts below are manual estimates (an issue can belong to two
  groups) and are meant as "how often", not as exact statistics.
- Not read: the closed-source parts (installer, Discord), the Discord server (where much of
  the support happens), and the WASM gauge source (only the compiled gauge is in the repo).

## 1. How it works

### Architecture in one paragraph

A Rust desktop app on each PC talks to the sim over SimConnect. A small WASM gauge plus
JavaScript files in the MSFS Community folder give the app access to things SimConnect cannot
reach (L: vars, calculator code, H: events, instrument input). One player is the "server"
(host), the others are clients. The app watches a list of variables and events from an
aircraft definition file, and every change is sent to the other side, where it is written
into the sim. That is all: **change is observed, sent, written. Nothing is read back.**

### Transport and networking

- UDP only, using the Laminar library (reliable-ordered, reliable-unordered and
  unreliable-sequenced packet modes on top of UDP). Messages are MessagePack, compressed with
  zstd. See [messages.rs](https://github.com/Sequal32/yourcontrols/blob/master/src/yourcontrols-net/src/messages.rs).
- Topology is a star: the host is the hub. Each client sends to the host, the host relays to
  everyone else. Clients never talk to each other.
- Three ways to connect: (1) direct to the host's IP and port (default 25071) with optional
  UPnP port mapping on IPv4; (2) "Cloud P2P": a rendezvous server hands out session IDs and
  does UDP hole punching (5 retries, 1 s apart); (3) "Cloud Host": a hosted server process
  acts as the host, so nobody needs a port open. That process only relays and remembers who
  is in control; it holds no variable state
  ([hoster state](https://github.com/Sequal32/yourcontrols/blob/master/src/yourcontrols-hoster/src/hoster/state.rs)).
  IPv6 is supported.
- Liveness: heartbeat every 1 s, idle timeout is a user setting (default 5 s, raised by users
  to survive long distances – #73).
- Handshake checks: exact app version must match (the server also has a minimum version) and
  the player name must be unique. Nothing else is compared.
- There is no automatic reconnect. When the connection drops the session ends and everyone
  joins again by hand.

### How changes are detected and sent

- **Aircraft variables (A: vars)**: one SimConnect request asks the sim for the listed
  variables every frame, but only when changed.
- **L: vars**: read through the WASM gauge, which pushes changed values to the app.
- **Key events (K: events)**: the app subscribes to a list of sim events and records each
  firing (with its number parameter).
- **H: events and instrument input (JS)**: captured by the JavaScript files inside the
  cockpit instruments (touchscreens, FMCs, tablets) and sent as "interaction" or "input"
  messages with an instrument name and an element ID. Time of day goes the same way.
- Detected changes are collected for one tick and sent as one update message with three lists:
  A: vars, L: vars, events. Every update is stamped with the sender's wall clock time.
- **Echo suppression** is a timer, not a proof: after writing a remote value into var X, local
  changes to X are ignored for 1 s so they do not bounce back. Same for events. A real local
  change inside that second is not forwarded. (The other side of the same coin: a remote
  change inside that second wins.)
- The app can never say "this write took effect". It sends and forgets.

### Variable types in the aircraft definition (profile)

Profiles are YAML ([example folder](https://github.com/Sequal32/yourcontrols/tree/master/definitions)).
Three top-level groups decide **who may write**: `shared` (anyone), `master` (only the pilot
currently in control), `server` (only the host, e.g. fuel and payload), plus an `init` group
(applied once from the other side at join). Entry types, in short:

| Type | What it does |
|---|---|
| plain variable (`var`) | Mirrors an A: or L: var as a value. Optional interpolation, optional update interval. |
| ToggleSwitch | Mirrors a boolean var by firing a toggle event (or separate on/off events) until the var matches. |
| NumSet | Mirrors a number by firing a "set to value" event, with multiply/add to convert units. |
| NumIncrement | Mirrors a number by firing "increase" / "decrease" events by the difference. Used for encoders that have no set event. |
| NumDigitSet | Sets a number digit by digit (radios, transponder). |
| event | Mirrors a K: event or H: event (optionally via calculator code). |
| proxy / multiply-difference / reset-when-equals | Small helpers for vars that behave oddly (value jumps back to 0 after a press, etc.). |
| program action | Triggers an app action, for example "take control" or "transfer control" when a cockpit button is pressed. |

Common options:

- **`unreliable`**: the value is sent as an unreliable-sequenced packet. Lost packets are not
  resent and old ones are dropped, so the latest value wins. Meant for continuous things like
  throttle, brakes, flight controls. Everything else goes reliable-ordered.
- **`interpolate`**: the value is not written directly but handed to the gauge, which glides
  the var to the new value between updates (variants for wrap-around at 90/180/360 degrees
  and for inverted values). Used for position/attitude and also (in the Fenix profile) for
  FCU encoders.
- **`cancel_h_events`**: when this var or event changes, the app ignores any captured H: or
  JS events for the next 300 ms. Reason: some controls fire an H: event as a side effect of
  the var or event the app just wrote. Without this the side effect would be recorded as a
  new user action and bounce back. It is a time-based filter, so a real button press inside
  those 300 ms is lost.
- **conditions**: an entry only applies if another var has a given value, with and/or.
- Remote events are queued and executed one per app tick, and H: events at least 50 ms apart.
  Fast typing queues up instead of being applied at human speed.

The host's profile is sent to every joining client (compressed) and used there, so everybody
runs the host's file. A client whose installed aircraft has a different version still uses the
host's definition and nothing warns about the mismatch.

### Control transfer

- Exactly one player is "in control" (the host at start). Transfer is a message "from X to Y",
  triggered from the UI, from a cockpit button mapped to a program action, or from a bound
  joystick button.
- The one who has control sends the master variables. The one who does not has the
  aircraft's attitude, altitude and lat/lon **frozen** with the sim's freeze events, and
  receives position as written values. A client re-freezes itself if a freeze flag reads wrong.
  See [freezer.rs](https://github.com/Sequal32/yourcontrols/blob/master/src/yourcontrols/src/sync/freezer.rs).
- Players can be set to observer, which stops their updates from being applied (used for
  VATSIM, where the second sim must not appear as a second aircraft).
- When the player in control disconnects, the host takes control back.
- On every transfer both sides clear their pending changes; there is no handshake that proves
  both sims are in the same state afterwards.

### Position and flight model

- Before v2.5.0 the mod used sim "active pause" to freeze the aircraft while syncing position.
  That caused jumps and was replaced (#61).
- Now the controlling sim sends position, attitude, velocity and control surfaces as fast as
  it reads them (the rate setting users tried at 10–60 Hz was removed in 2.7.8). They are
  marked interpolated and unreliable, and the gauge blends toward them in the other sim. The
  sim that is not in control is frozen (see above), so it does not simulate its own physics.
  Details and a comparison with our follower spike: section 6.
- A wind-component corrector existed (to keep ground speed equal in different weather) but
  the changelog for 2.8.11 says it was removed.
- On MSFS 2024 the changelog for 2.8.15/16 still lists ground jitter as "semi-fixed, WIP".

### What happens on connect and reconnect

1. Client sends version and name, host checks both and sends the list of players and the
   aircraft definition.
2. Both sides wait 3 s ("allows L: vars to be processed"), then the client sends "ready".
3. The player in control answers with a **full dump of all A: and L: vars it knows**. Events
   (button presses, FMC key presses, tablet input) are not part of the dump – they are gone.
4. After that, only changes are sent.

So the initial alignment is "overwrite the client with the controller's current values" with
no comparison first and no check afterwards. Anything not in a variable (FMC contents, tablet
screens, flight plans) starts different and stays different.

### Is anything ever read back or confirmed?

Not at application level. The code has no acknowledgement per change, no checksum or hash of
state, no periodic comparison and no desync alarm (I searched the app, network and definition
code). Reliability comes from the packet layer: Laminar resends lost reliable packets. Even
there, one limit showed up once: the heartbeat did not ask for an acknowledgement, so dropped
packets were not resent and one direction stopped while the link still looked healthy (#64,
addressed in 2.4.1). For FMC drift the developers' guidance is "do not type too fast" and
"both need the exact same nav data" (#190). This is the area Crosscheck's verified sync is
designed to cover.

### The Fenix profile specifically

- The profile in the repo targets **Fenix 2.4.0.3311**, not 3.0. (The 2.8.12 file lists it as
  verified for both MSFS 2020 and 2024.) Roughly 1,600 active L: var entries, mostly plain
  mirrors, plus 20 NumSet, 5 ToggleSwitch, 2 events. About 1,000 further entries are
  commented out.
- FCU encoders (`E_FCU_*`) and MCDU keys (`S_CDU*_KEY_*`) are mirrored as plain values. The
  FCU encoders are marked interpolated.
- The 2.8.9 changelog documents the known Fenix limits openly: button and knob variables
  "count to infinite" (desync may occur), fuel variables are only intermittently writable, the
  right wing tank does not fully refuel, the cargo door logic is disabled, standby frequencies
  can differ, transponder 2 is not synced, brake temperatures are not synced.
- Users asked for Fenix support from 2022 (#156), a Fenix frame-rate drop went away when the
  mod was removed (#157), and a June 2026 report describes the aircraft rotating on the client
  after a control transfer (#314, open).

## 2. Known issues and limits (from the issue tracker)

Ordered by how often they show up. "≈" numbers are my manual count of problem reports (not
requests) per group; groups overlap. The last column is my reading of the underlying cause –
mostly limits of the approach or of what the simulator and aircraft expose.

| # | Group | ≈ Count | Examples | Underlying cause (my reading) |
|---|---|---|---|---|
| A | Individual switches/knobs/lights out of sync, or in sync one way only | 30 | [#6](https://github.com/sequal32/yourcontrols/issues/6) (one plane starts on join, other stays off), [#29](https://github.com/sequal32/yourcontrols/issues/29) (list of unsynced items on A32NX), [#94](https://github.com/sequal32/yourcontrols/issues/94), [#130](https://github.com/sequal32/yourcontrols/issues/130) (gear lever), [#305](https://github.com/sequal32/yourcontrols/issues/305) (GNS one way) | The profile is a list of what to mirror, written per aircraft. Anything not on the list, not exposed to SimConnect, or with unusual behaviour (a momentary L: var that returns to 0 before the sim reads it, #29) is not synced, and without read-back there is no signal when that happens. Initial alignment is a one-time overwrite. |
| B | MCDU / FMC / G1000 / tablets: screens differ, key presses lost | 15 | [#8](https://github.com/sequal32/yourcontrols/issues/8), [#25](https://github.com/sequal32/yourcontrols/issues/25) (different nav data), [#190](https://github.com/sequal32/yourcontrols/issues/190) (CJ4), [#250](https://github.com/sequal32/yourcontrols/issues/250), [#266](https://github.com/sequal32/yourcontrols/issues/266) (open: missing waypoints, ignored presses) | The state of these screens is internal to the aircraft, so key presses are replicated instead of the result. One lost or reordered press, a different nav database, or a press inside the echo/H-event suppression windows leaves the two MCDUs on different pages. Presses are applied one per tick, which limits fast entry. |
| C | AP / FCU / encoders: values jump, reset to 0, managed mode stuck | 12 | [#12](https://github.com/sequal32/yourcontrols/issues/12) (AP on control pass), [#134](https://github.com/sequal32/yourcontrols/issues/134) (HDG/VS reset to 000), [#172](https://github.com/sequal32/yourcontrols/issues/172), [#292](https://github.com/sequal32/yourcontrols/issues/292) (stuck in managed) | Knobs that are counters or events inside the aircraft are mirrored as absolute values. When an aircraft replaces a var by an H: event (FBW managed speed knob, #134) it cannot be synced directly, and the practical answer is that the person in control operates it. Both sides can write the same knob. |
| D | Position and flight model: planes apart, jumps, drift, banking after transfer | 12 | [#16](https://github.com/sequal32/yourcontrols/issues/16) (plane jumps ahead after transfer), [#51](https://github.com/sequal32/yourcontrols/issues/51) (speed 300 kt after reconnect), [#61](https://github.com/sequal32/yourcontrols/issues/61), [#137](https://github.com/sequal32/yourcontrols/issues/137) (plane ahead above 220 kt), [#237](https://github.com/sequal32/yourcontrols/issues/237), [#314](https://github.com/sequal32/yourcontrols/issues/314) | Holding one sim's aircraft frozen while writing position into it works against the sim's own physics. The earlier pause-based approach was replaced in 2.5.0 (#61); the current one still drifts with speed and latency, and a transfer swaps freeze flags on both sims without a check that attitude matches. |
| E | Connection: timeouts, NAT, firewall, "peer timeout", one-way stall | 15 | [#5](https://github.com/sequal32/yourcontrols/issues/5), [#64](https://github.com/sequal32/yourcontrols/issues/64) (server keeps receiving but stops sending), [#73](https://github.com/sequal32/yourcontrols/issues/73) (EU–Canada disconnects), [#138](https://github.com/sequal32/yourcontrols/issues/138), [#262](https://github.com/sequal32/yourcontrols/issues/262), [#202](https://github.com/sequal32/yourcontrols/issues/202) (asks for TCP) | UDP with a short idle timeout; port forwarding, UPnP and hole punching depend on the router and do not work on every network or VPN; the host's connection is the whole session. A lost connection ends the session and players rejoin by hand. |
| F | Install and runtime link: WASM gauge not loading, "could not connect to the gauge / SimConnect" | 20 | [#91](https://github.com/sequal32/yourcontrols/issues/91), [#151](https://github.com/sequal32/yourcontrols/issues/151) (8 comments; non-ASCII Windows user name), [#257](https://github.com/sequal32/yourcontrols/issues/257), [#251](https://github.com/sequal32/yourcontrols/issues/251) (p42 Flow panel interferes), [#313](https://github.com/sequal32/yourcontrols/issues/313) (installer sees another program with "2024" in its name as a running sim) | Reaching L: vars and instrument input needs a WASM module in the Community folder plus JS in the instruments. MSFS does not load some modules (path characters, other mods), and other add-ons that touch the same instruments can interfere. |
| G | Crashes and performance | 8 | [#35](https://github.com/sequal32/yourcontrols/issues/35) (low FPS), [#104](https://github.com/sequal32/yourcontrols/issues/104) (CTD after sim update, any mod with a modules folder), [#157](https://github.com/sequal32/yourcontrols/issues/157) (Fenix FPS drops after ~1 h, gone when removed), [#225](https://github.com/sequal32/yourcontrols/issues/225), [#231](https://github.com/sequal32/yourcontrols/issues/231) (75 % FPS drop in 737 EFB) | The gauge runs inside the sim process, so any issue in it shows up as a sim crash or frame-rate loss. Changelog entries on frame-rate improvements for FBW, Headwind and PMDG show it was an ongoing tuning topic. |
| H | Version and profile drift (sim updates, aircraft updates, mismatching data) | 8 | [#25](https://github.com/sequal32/yourcontrols/issues/25) (nav data), [#39](https://github.com/sequal32/yourcontrols/issues/39) (SimConnect problems after a sim update), [#213](https://github.com/sequal32/yourcontrols/issues/213) (Asobo 747/787 controls inactive after update), [#292](https://github.com/sequal32/yourcontrols/issues/292) (dev build "not supported") | Profiles are maintained by hand against one aircraft build. A sim or aircraft update can rename or remove a var, and it is found when a user reports it. The Fenix profile still targets 2.4.0.3311. The handshake checks the app version, not the aircraft version, the profile or nav data. |
| I | Control transfer itself | 7 | [#12](https://github.com/sequal32/yourcontrols/issues/12), [#31](https://github.com/sequal32/yourcontrols/issues/31), [#143](https://github.com/sequal32/yourcontrols/issues/143) (joystick take-over stopped working), [#147](https://github.com/sequal32/yourcontrols/issues/147) (open: finer-grained control) | One flag for the whole aircraft; transfer is a message without confirmation that both sides applied it. |
| J | MSFS 2024 | 5 plus all profile limits | [#275](https://github.com/sequal32/yourcontrols/issues/275) (9 comments, requests for a year), [#313](https://github.com/sequal32/yourcontrols/issues/313); changelog 2.8.9–2.8.15: many 2020 aircraft elements needed work on 2024, position freeze, ground jitter, touch screens, time sync | The repo is quiet from early 2024 until a burst of work in March–April 2026. 2024 support is a second set of profiles (11 aircraft vs ~150 for 2020); many 2020 profiles run on 2024 with fixes. |
| K | Fenix / A320 specific | 4 issues plus the changelog list | [#156](https://github.com/sequal32/yourcontrols/issues/156), [#157](https://github.com/sequal32/yourcontrols/issues/157), [#246](https://github.com/sequal32/yourcontrols/issues/246), [#314](https://github.com/sequal32/yourcontrols/issues/314); "Fenix A320 Issues" list in the 2.8.9 changelog | Counter-style button/knob vars mirrored as absolute values, fuel only intermittently writable, door logic non-linear. Support arrived in 2.8.9 and targets an older Fenix build. |
| L | VATSIM / observer | 3 | [#16](https://github.com/sequal32/yourcontrols/issues/16), [#44](https://github.com/sequal32/yourcontrols/issues/44) (second sim shows up as an AI aircraft on top of the cockpit) | The second sim is a full sim connected to the network. It needs a callsign suffix and the observer option; with a wrong setting the pilot sees a ghost aircraft. |

Smaller things worth remembering:

- Fuel: the host owns fuel on purpose ("what he has you have", #139). An early profile had fuel
  as pilot-controlled and it conflicted with itself until it was moved to the host group.
- Several fixes were of the type "pressed button X, the sim fires H: event Y, which was echoed
  back" (the `cancel_h_events` work). This class of issue comes with having no ordering or
  origin tag on writes.
- Many answers on issues are "ask on Discord", "reinstall", "restart the sim". Diagnosis is
  done by hand: users attach logs from both PCs (the bug template asks for them).

## 3. How Crosscheck approaches this

| Group | Crosscheck approach | Status |
|---|---|---|
| A. Individual controls | **Verified sync**: every write is read back from the own L: var in 1–2 frames, then effect via I_/N_ var or GraphQL where available. **Profile from our own scan per aircraft build**, every var in a sync class (`ignore` is an explicit choice). **Snapshots** per panel group every ~2 s, diff on mismatch. | Covered by design. Open point: controls that live only inside Fenix code with no L: var or GraphQL value can be read back but not verified; the profile should list them as "unverifiable". |
| B. MCDU / FMC | **Pulse** class for `S_CDU*_KEY_*` with sequence numbers from the server, effect checked on the MCDU screen text from GraphQL (14×24 chars). Snapshot includes a hash of both MCDU screens. | Mostly covered. Open points: (1) screens can legitimately differ if nav data differs – needs a **preflight check** of AIRAC / nav data versions, only possible if Fenix exposes it (not yet known); (2) key → screen takes ~450 ms inside Fenix, so the verify window is longer than for switches; (3) what to do on a mismatch without writing a "correction" the PF did not make. |
| C. FCU / encoders | **Delta** class for `E_FCU_*` (send the change, never the absolute) with the display var `N_FCU_*` as proof and a corrective delta on mismatch. | Covered by design, untested on Fenix 3.0. Open point: both pilots turning the same knob at once; the control token decides. |
| D. Position / flight model | Only the PF flies; position/attitude/velocity/sidestick stream at 20–30 Hz, the other sim follows with interpolation and dead reckoning. Stream class is "latest wins", no read-back. | **Open risk.** We have not yet shown that the follower can be held stable on MSFS 2024; YourControls also lists ground jitter there as work in progress. Needs a dedicated spike, including what a PF role transfer does to the aircraft that was following. |
| E. Connection / NAT | **Relay server** (WebSocket, TCP) with sequence numbers and roles, so no port forwarding between players. After a short outage the snapshot diff resyncs. | Partly. Phase 1 runs the server locally, so the two-computer test needs a reachable address (LAN or tunnel). A hosted relay is not planned yet = **gap** for real users. Reconnect and resync behaviour still has to be designed and tested. |
| F. Install / WASM / SimConnect link | The bridge is an ordinary Windows program using SimConnect and GraphQL. No WASM or Community-folder module is needed to fly (the scanner module is read-only and not for flying). | Covered for the WASM-loading class (path characters, other mods, add-ons touching the same instruments). Not covered: SimConnect itself not connecting, firewall rules for localhost GraphQL, non-ASCII Windows user names (to test), and sim detection by process name (we should not rely on it, see #313). |
| G. Crashes / performance | The bridge runs outside the sim; if it stops, both sims keep flying unsynced. | Covered for crashes. Open point: our GraphQL polling and L: var reads cost Fenix frame time (see #157). Must be measured, with poll rate as a setting. |
| H. Version / profile drift | **Profile generated per aircraft build**; a **preflight gate** compares Fenix package version, profile hash, Crosscheck version and MSFS build on both PCs and declines to start on a mismatch (or states exactly what differs). | Covered by design. Open point: the Fenix build number must be readable at runtime (GraphQL or package folder) – confirm before promising it. |
| I. Control transfer | **Control token** on the server, with ack from both sides; write permission follows the token. | Covered by design. Open point: the snapshot should match before the token moves. |
| J. MSFS 2024 | We target 2024 only; L: vars are proven there (phase 0). | Covered. 2020 is out of scope. |
| K. Fenix specifics | Counters via delta, fuel/payload via **owned** class with a drift display, doors/brake temp via observe or ignore. | Covered by design; Fenix 3.0 renames vars (`E_CB_*` etc.) and semantics are still being verified. Fuel writability needs a real test (YourControls reports intermittent writes). |
| L. VATSIM / observer | – | **Not covered.** Two sims online with the same callsign is a setup matter. The UI should say that only the PF sim may be connected to VATSIM and, where possible, detect and warn. |
| One-way stall (#64) and any other silent difference | **Sequence numbers + snapshots + visible alarm**: a stall cannot go unnoticed for longer than one snapshot interval. | Covered by design. Pick the alarm timing now (2 s snapshots, how many missed before it turns red). |
| Echo / feedback loops | Server ordering plus `origin` on every command, so a write is recognised by identity instead of a 1 s timer. | Covered by design – keep time-window filters out of the bridge. |
| Fast key entry | Pulse sequencing and ordering instead of a one-per-tick queue. | Covered by design; test with fast typing. |

Open points in short: follower position on MSFS 2024, a hosted relay for real users,
reconnect/resync, controls that cannot be verified, nav-data parity, VATSIM setup, and the
performance cost of polling.

## 4. Test cases for the two-computer test (CRO-15)

Each line: setup – action – pass. "A" = PF sim, "B" = other sim, both cold & dark Fenix A320 on
the same parking position unless stated.

1. Join into a cold & dark cockpit: connect B – compare all panel-group snapshots – every
   group matches or each difference is listed by name (nothing silent).
2. Join into a powered cockpit (batteries on, APU running): connect B late – wait for first
   snapshot – B's switches, lights and MCDU match A within the join sequence.
3. Connect while the controlling side keeps pressing switches: A toggles ten switches during
   join – B ends up identical, no switch stuck in an old state.
4. Battery, APU master, ext power, packs: A toggles each – B shows the same, verify result
   visible in the UI (confirmed, not just sent).
5. A flipped switch that Fenix rejects (e.g. a guarded or interlocked one): A requests it –
   UI shows "not confirmed", no silent success.
6. Light switches (seat belts, landing, taxi, strobe): toggle on both sides in turn – both
   sides always end identical (YC #13, #135).
7. Gear lever and flaps lever: A moves, then B (as non-PF) tries – result follows the control
   rules and both agree afterwards (YC #130).
8. FCU encoders: turn HDG, SPD, ALT, V/S, baro quickly in both directions on A – B's display
   values match exactly after 2 s, no jump to 000 (YC #134, #172).
9. FCU: push and pull each knob (managed/selected) – both FCUs show the same mode (YC #292).
10. FCU: both pilots turn HDG at the same time – both ends on the same value, no tug of war.
11. Autopilot engaged, then control token passes to B – autopilot stays engaged and unchanged
    on both sides (YC #12).
12. MCDU: type an INIT page, flight plan, and perf data on A with normal speed – B's MCDU
    screen text equals A's on every page.
13. MCDU: type the same entry very fast (hold a key, mash LSKs) – no key lost; any mismatch is
    flagged within one snapshot (YC #190, #266).
14. MCDU: both pilots type on their own MCDU at once – both screens stay valid, mismatch is
    shown if it occurs.
15. MCDU with different AIRAC on B – preflight gate refuses or names the difference (YC #25).
16. Pulse buttons: ECAM CLR / RCL / STS, MASTER CAUTION and MASTER WARNING – count of presses
    equal on both, odd/even state equal (CLAUDE.md special case).
17. Fuel and payload: set at session start on host, change fuel with the EFB on A – B follows
    or shows drift in kg (YC #139, changelog fuel notes).
18. Breakers (`E_CB_*`): pull and push a safe one – B's breaker state follows and its system
    effect shows (never use fire/IDG/ENG MASTER/EVAC/oxygen/ditching).
19. Standby frequencies and transponder 1 and 2: enter on A – B shows the same on all (YC
    changelog: standby freq and XPDR 2 are listed as not synced).
20. Packet loss: add 5 %, then 20 % loss for 60 s while A flips switches – everything converges
    after loss ends; no permanent difference (YC #64).
21. One-way stall: block traffic A→B only for 10 s – an alarm appears on both sides within the
    snapshot interval, nothing stays silently different (YC #64).
22. Full disconnect and reconnect: kill B's network for 30 s while A changes switches and
    MCDU – on return B is resynced and the preflight/snapshot view says so; nothing is
    "corrected" in A's sim without a pilot action (YC #51 reconnect).
23. High latency (200–300 ms RTT, jitter): flip 20 switches – no lost update, no reorder, ack
    times shown (YC #73).
24. Process crash: kill the bridge on B – sim keeps flying; restart – rejoin works and resyncs.
25. Process crash on the server – both sims keep flying; restart and rejoin work.
26. Version mismatch: different Fenix build, different Crosscheck build, different profile hash
    on B – preflight refuses and names the difference (YC #213, #292).
27. Taxi at 20 kt, then takeoff to 250 kt (PF = A): compare A and B positions – difference
    stays inside the target envelope with no jumps (YC #137, 2024 jitter).
28. Ground handling: parked for 10 min with parking brake – B's aircraft does not drift or
    jitter (YC #77, changelog 2.8.15).
29. Control transfer on the ground and in the air: A passes PF role to B – both aircraft keep
    heading, attitude and speed; no banking or spin (YC #16, #237, #314).
30. Observer and VATSIM: A on VATSIM, B not – B does not appear as a second aircraft, UI warns
    if both are online (YC #44).
31. Performance: fly 90 min with the bridge on – frame rate on A and B equals the no-bridge
    baseline within agreed tolerance (YC #157, #35).
32. Third-party add-ons running (GSX, FSRealistic, Chaseplane, p42 Flow): board and push back
    with GSX – nothing breaks and add-on vars stay `ignore` (YC #251, #314).
33. Unusual Windows setup (non-ASCII user name, no admin rights, firewall on): installation and
    first connect work or give a clear message (YC #151, #257).
34. Sim restart on B mid-session: B reloads the flight – rejoin and resync without restarting
    A.
35. Safety: during all tests, check that Crosscheck never pauses, changes sim rate, moves A,
    or changes weather/time; log shows no write outside profile ranges.

## 5. What YourControls does well – and what we want to match

- **Zero-config start**: install, pick aircraft, share a code; the host's profile goes to
  clients automatically, so nobody needs matching files. We should keep "no manual file
  copying" and send the generated profile in the session.
- **Several ways to connect** (direct, UPnP, hole punching, hosted relay, IPv6). Our relay
  should make the common case a single code or link.
- **Who may write is explicit** (shared / master / host-only groups). Matches our roles; keep
  it visible in the UI.
- **Host owns fuel and payload** – simple rule that avoids conflicting writes; same as our `owned` class.
- **Observer mode** for VATSIM and for spectators.
- **Take control with a joystick button or cockpit button**, not only the UI – pilots love not
  leaving the cockpit view.
- **Wide aircraft coverage** (~150 profile files for 2020) and an active user community on Discord.
  We start with one aircraft; the scanner-to-profile pipeline is our answer.
- **Practical settings**: update rate, connection timeout, Discord presence, a built-in
  updater, and a bug template that asks for both players' logs. Worth copying: timeout and
  rate settings and a log file both sides can attach. Users also asked for hiding the IP
  address (#15) and auto-loading a saved config at sim start (#111).
- **Emulator** to test profiles without a sim
  ([emulator.rs](https://github.com/Sequal32/yourcontrols/blob/master/src/yourcontrols/src/emulator.rs)):
  a good idea for our own CI.
- **Free and open**, with a maintainer team that kept it alive for six years. We should
  match the openness and the quick response to sim updates.

## 6. Follower: how YourControls and FS Copilot place the aircraft

Research for CRO-17, written 2026-11-04. It compares how both projects move the follower
(the sim that is not flying) with our follower spike
([docs/spikes/CRO-17-follower.md](../spikes/CRO-17-follower.md)).

[FS Copilot](https://github.com/yury-sch/FsCopilot) by Yury Scherbakov is a newer shared
cockpit for MSFS 2020 and 2024 (C# app plus a WASM module, first release 2026). It is
**MIT-licensed**, like Crosscheck. We still describe it in our own words and copy none of its
code, the same as for YourControls (GPL-3.0). Both projects solved this problem in public, and
we learn from what they found.

Sources: YourControls code at `4e32af5` (2026-04-05) with its full history, its changelog,
GitHub releases (old and new repo) and issues. FS Copilot code at `7b85a16` (default branch
`main`) with its full history and its releases. FS Copilot has no GitHub issues (support is on
Discord). Limits: the source of the YourControls WASM gauge, where its interpolation runs, is
not in the repo, only the compiled module. What we say about that interpolation comes from
the symbol names in the module, the app's side of the protocol and the older Rust
interpolator it replaced, so it is marked "probably". We did not read either Discord.

### 6.1 What is sent

| | YourControls | FS Copilot |
|---|---|---|
| Position | `PLANE LATITUDE`, `PLANE LONGITUDE` (degrees), `PLANE ALTITUDE` (feet, above sea level) | the same (lat/lon in radians) |
| Attitude | `PLANE PITCH DEGREES`, `PLANE BANK DEGREES`, `PLANE HEADING DEGREES TRUE` and `… GYRO` (the gyro heading "due to drift") | the same, in degrees |
| Velocities | `VELOCITY BODY Y`, `VELOCITY BODY Z`, `VELOCITY WORLD X`, `VELOCITY WORLD Z`, `VERTICAL SPEED`, `G FORCE` | the same set |
| Not sent | `VELOCITY BODY X`, `VELOCITY WORLD Y`, rotation rates, accelerations, on-ground, height above ground, wheels | the same |
| Control surfaces | `AILERON/ELEVATOR/RUDDER POSITION`, written with the `AXIS_*_SET` events, only while the autopilot is off | the same three, written both as the A: var and with the `AXIS_*_SET` event |
| Rate | as fast as the sim reports changes, so about once per sim frame. The rate setting was removed in 2.7.8 | fixed 20 Hz (every 50 ms) |
| Transport | UDP (Laminar), unreliable-sequenced: lost packets are not resent, older ones are dropped | UDP (LiteNetLib), sequenced: the same idea |
| Time stamp | sender's wall clock at send time | sender's stopwatch (ms since app start) |

Sources: [YC physics.yaml](https://github.com/Sequal32/yourcontrols/blob/master/definitions/FS2024/modules/physics.yaml),
[FSC Physics.cs](https://github.com/yury-sch/FsCopilot/blob/main/FsCopilot/Simulation/Physics.cs).
The YourControls file marks the velocity block "do not change or the autopilot will break". The
velocities are not used to predict position. They are written so that the follower's speed
indications and autopilot see a moving aircraft, and so that the aircraft already has its
speed when the freeze is lifted at a handover.

### 6.2 How the follower writes

Both write from **inside the sim**: a WASM module receives the latest values and writes
position, attitude and the velocities above **once every sim frame**, through calculator code
(`execute_calculator_code`). YourControls moved this into its gauge in January 2021. FS
Copilot did the same in January 2026 (before that it wrote with `SetDataOnSimObject` at the
network rate). Since 1.1 it interpolates "on every simulator visual frame", and its release
notes credit this with removing stutter, jumps and jitter. In MSFS 2024 its module runs on the
frame update of a standalone WASM module.

### 6.3 Smoothing, buffering, late updates

| | YourControls (probably, see limits) | FS Copilot |
|---|---|---|
| Clock | playback clock starts at the first packet's sender time and runs on local time; it fast-forwards if it falls more than 1 s behind | offset to the sender clock per session, smoothed with factor 0.01 per packet |
| Buffer | waits until the newest packet is about 100 ms ahead | plays 0.4 s behind (was 0.8 s until 2026-02-21); keeps only one sample per 0.2 s as a keyframe, so about 5 points per second |
| Interpolation | linear between the two packets around the playback time; angles the short way round (wrap types for ±90°, ±180°, 0–360°) | linear between keyframes; angles the short way round |
| Late update | holds the last value (no prediction) | holds the last value (no prediction, no snap limit) |

Neither project predicts ahead, and neither uses the velocities for the curve. Both rely on a
straight line between points plus a buffer large enough that a point is almost always there.

### 6.4 Stopping the follower's own physics, and the ground

- **Both use the three freezes**, the same as our method B: `FREEZE_LATITUDE_LONGITUDE_SET`,
  `FREEZE_ALTITUDE_SET`, `FREEZE_ATTITUDE_SET`. Neither uses slew or pause (YourControls used
  active pause before 2.5.0 and replaced it, #61).
- Both watch `IS LATITUDE LONGITUDE FREEZE ON`, `IS ALTITUDE FREEZE ON` and
  `IS ATTITUDE FREEZE ON` every frame and send the freezes again if one is wrong.
  - YourControls has sent them as calculator code through its gauge since the "2024 band-aid"
    in 2.8.11 ("fixed position freeze on FS2024"). The SimConnect events alone were not
    reliable on 2024.
  - FS Copilot sets them from its WASM module and renews "who flies" every 200 ms. Its module
    lifts the freezes by itself if the app goes quiet for more than 1 s.
- **No ground handling in either.** No on-ground flag, height above ground, wheel or contact
  logic, no taxi mode. On the ground the follower gets the same sea-level altitude and
  attitude as in the air.
- YourControls once had two corrections, and removed both as buggy:
  - altitude near the ground for different scenery (below 1000 ft above ground): altitude
    jumped at the 1000 ft threshold;
  - speed for different wind: the aircraft "launched forward on the ground" with spinning
    wheels (removed in 2.6.1, and again in 2.8.11).

### 6.5 Handover

Neither does anything special.
- The new pilot's side stops interpolating and lifts its freezes. The old side freezes and
  starts playing what it receives.
- No velocities are seeded at that moment: the aircraft already has them, because they were
  written every frame while it was frozen.
- There is no check that both sims agree before or after.
- FS Copilot can briefly have two sides that both think they fly (last writer wins).

### 6.6 Known problems and fixes

| Problem | Where | Fix or status |
|---|---|---|
| Aircraft hundreds of metres apart, FD offset, wrong direct-to | YC [#61](https://github.com/sequal32/yourcontrols/issues/61) | Cause was active pause; replaced by freezes in 2.5.0 |
| Jump or teleport at handover, plane jumps ahead | YC [#12](https://github.com/sequal32/yourcontrols/issues/12), [#16](https://github.com/sequal32/yourcontrols/issues/16), [#77](https://github.com/sequal32/yourcontrols/issues/77) | Several fixes in 2.5.2–2.6.2 ("fixed clients teleported on transfer", "attempted to fix random warping"); no cause written down |
| Interpolation breaks a handover in a steep bank, "teleport into the ocean" | YC 2.6.0 release notes | Advice: hand over wings level. 2.6.0/2.6.1 also fixed wrong angle units (degrees vs radians) and a jump at heading 360° |
| Launching forward on the ground, wheels spinning | YC 2.6.1 | Wind/speed "physics corrector" removed |
| Altitude jumping below 1000 ft; plane 2 m above or in the ground | YC 2.5.7, [#201](https://github.com/sequal32/yourcontrols/issues/201) | Ground-altitude correction removed; #201 closed without a code fix (scenery difference) |
| Banking or rotating after a handover | YC [#237](https://github.com/sequal32/yourcontrols/issues/237), [#314](https://github.com/sequal32/yourcontrols/issues/314) (Fenix, open) | Open or closed without a cause |
| Ground jitter on MSFS 2024 | YC 2.8.16 release notes (April 2026): "semi-fixed, better but still present, WIP"; 2.8.17 went back to the older WASM "due to instability" | Open. A May 2026 post on the [MSFS dev support forum](https://devsupport.flightsimulator.com/t/constant-jittering-only-on-2024-what-changed/17842) describes it. It happens only on 2024, and also when only lat/lon is written. The aircraft itself moves smoothly, but the world seems to shake, more with speed and reportedly more at higher frame rates. No answer from Asobo yet |
| Small jumps and jitter | FSC 1.0-rc.2/rc.3 | Cause: network updates arriving out of order (fixed); then 1.1 interpolates on every visual frame |

### 6.7 Compared with our follower spike

| | YourControls | FS Copilot | Our spike (CRO-17) |
|---|---|---|---|
| Freeze | three freezes, re-sent if off | the same, plus a watchdog in the sim | method B: the same (A: none, C: slew) |
| Writes | position, attitude, body Y/Z + world X/Z + vertical speed + G force, every frame, from inside the sim | the same | position, attitude, three body **or** three world velocities and three rotation rates, from outside the sim through SimConnect, once per state message |
| Curve | linear | linear, ~5 points/s | cubic curve through the frames (velocity or `fd` tangents), attitude as a rotation |
| Buffer | ~100 ms | 400 ms | 100 ms, clock from the fastest arrival |
| Late | hold | hold | predict up to 250 ms, then hold, then blend back over 200 ms |
| Ground | nothing special | nothing special | nothing special |
| Clock | sender wall clock; local frame time | sender stopwatch; local frame time | PC stopwatch at the moment a SimConnect message is handled (recorder and player) |
| Measured | no | no | per phase: error, jitter, spikes, reaction |

Method B is what both projects ship, so B is the right baseline. Our smoothing and prediction
already go further than theirs. Where we differ is **timing**: they write from inside the sim on
the sim's own frame; we time and write from outside it.

### 6.8 What it means for jerky taxi and the jumping ground speed (method B, `--tangents fd`)

Checked offline on `circuit1.cxrec` and the method B run of 2026-11-04 (file reading only, no
sim):

1. **The recording's time stamps are noisy.** The recorder stamps each frame with the PC's
   stopwatch when the message is handled, not with sim time.
   - 1179 frames (3.4 %) are less than 3 ms after the one before: two sim frames handled in
     one go.
   - 208 taxi frames repeat the position of the frame before.
   - Speed from position change ÷ stamped time swings between 55 % and 133 % of the real
     ground speed while taxiing (5th to 95th percentile).
2. **The velocity axes are fine; `fd` is the wrong cure.** `inspect` reported a 10 % mismatch
   between recorded velocity and position change, which led to `--tangents fd`. Over longer
   windows the mismatch shrinks to 3.5 % (0.1 s), 1.0 % (0.5 s) and 0.3 % (2 s), and the
   horizontal world velocity equals `GROUND VELOCITY`. So the 10 % is time-stamp noise. `fd`
   builds the curve from exactly those time stamps, so it turns the noise into uneven speed.
3. **The player has the same timing problem.**
   - On the taxiway, 240 of 8689 follower frames were less than 3 ms apart.
   - In about 2.5 % of taxi frames the sim showed a position about one frame behind what we
     had just written.
   - So a sim frame sometimes gets no new position, and the next one gets two frames' worth.
     The result is a stutter, and a speed that jumps if anything (the sim or the Fenix)
     derives speed from the position change.
4. **Our jitter metric measures the same noise.** It uses the stopwatch too, which is why
   nearly every taxi frame of the B run counts as a "spike". Until the clock is fixed, the
   jitter and spike columns cannot rank the methods.

For the methods:

- **B** matches both projects. Its jerkiness on our run most likely comes from our timing
  (points 1–3), not from freezing. Fix the timing first, then judge B again. Also write the
  same velocity set both projects write, so that the speed indications have a steady value to
  show.
- **A** is not used by either project. Expect the Fenix fly-by-wire to push back (the reaction
  columns). Still worth one run after the timing fix: it is the only method that leaves the
  aircraft fully alive at a handover.
- **C** (slew) is not used by either project. YourControls only mirrors the slew switch as a
  normal control. Keep it as a comparison, but no one has made it work for a follower.
- If B is still uneven after the timing fixes, the remaining step is what both projects did:
  write from inside the sim on every frame.
  - That needs a WASM module in the Community folder that writes position. This is an owner
    decision, because our scanner module is read-only by rule.
  - YourControls' open 2024 ground jitter suggests that even this may not be perfect on 2024.

### 6.9 Changes to try in the player

In order. The first four need no sim to build and test.

1. **Stamp frames with sim time.** Record `SIMULATION TIME` (and `ABSOLUTE TIME` as a second
   candidate) in every frame, next to the stopwatch time. `inspect` shows which one advances
   smoothly with the position, and the player and metrics use that one. Drop frames whose
   position and sim time both repeat the frame before. Then record `circuit1` again; the
   current recording cannot be fully repaired.
2. **Run the player's clock on sim time too.** Advance the render clock by the sim's own frame
   step, not by the stopwatch, so each sim frame moves the aircraft by exactly one frame's
   worth. When several state messages are handled in one go, write only once, for the newest.
3. **Make velocity tangents the default again.** Change the `inspect` check to compare over
   0.5 s windows (or over sim time), so time-stamp noise no longer looks like wrong axes.
   Keep `fd` only as an option.
4. **Compute jitter and spikes on sim time.** Also add the follower's read-back
   `GROUND VELOCITY` to `frames.csv`, so "the ground speed jumps" becomes a number.
5. **Write the velocity set both projects use**, as a new `--velocities` choice: body Y/Z,
   world X/Z, `VERTICAL SPEED`, and `G FORCE` if the sim accepts it. Also try
   `--velocities world` and `none` on B, to see which one steadies the ground speed display.
6. **A handover test on the ground.** At the end of a taxi segment at about 15 kt, release
   the freezes while moving (offline, own sim, as the stop key does now) and log the next 5 s.
   Does the aircraft keep its speed and heading, or does it jump or stop? Both projects had
   most of their reported problems at this moment.
7. **Only if B is still uneven after 1–5:** a test WASM that writes position on each sim
   frame, like both projects. Owner decision first: it would be our first module that writes.

Keep our prediction, blend and cubic curve. Neither project has them, and they matter on a real
network (loss and stalls). They are not related to the ground timing problem above.
