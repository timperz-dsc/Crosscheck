# CRO-17: follower spike

**Question:** can a follower aircraft be placed where the PF's aircraft is, every frame,
smoothly enough to sit in, at the gate, taxiing and in flight?

`docs/protocol.md` §9 leaves the `stream` field set open until this spike, and
[YourControls](../research/yourcontrols.md) (category D) shows that this is the open risk on
MSFS 2024. Round 1 needs only one PC: record a circuit in your sim, then play the recording
back into the same sim as the follower, through a simulated network, and compare three
placement methods by numbers.

Code: [spike/Follower/](../../spike/Follower/). The bridge does not use any of it.

## Safety rules

These tools are the only place where Crosscheck moves an aircraft or uses freeze or slew.
The owner allowed this for the spike only, on these terms:

1. **Offline only, your own sim only.** The player moves *your* aircraft. Never run it while
   connected to VATSIM, IVAO, PilotEdge or any multiplayer session. Before it connects, the
   player asks you to type `OFFLINE`. It also refuses to start while a known network pilot
   client is running (vPilot, xPilot, swift, IVAO Altitude, IvAp, PilotEdge). That check is
   only extra; your confirmation is what counts.
2. **What it writes:** the follower's position, attitude and velocities (with method B by
   default also `VERTICAL SPEED` and `G FORCE`; each stops at once if the sim refuses it),
   plus the events of
   the chosen method and nothing else: `FREEZE_LATITUDE_LONGITUDE_SET`, `FREEZE_ALTITUDE_SET`,
   `FREEZE_ATTITUDE_SET` (method B), `SLEW_ON`/`SLEW_OFF` (method C). Pause and sim rate are
   never sent; the code has no way to send them (a test fails if the words appear in the
   Windows host). The recorder writes nothing at all.
3. **Never part of the bridge.** The bridge's own tripwire still forbids all of this in its
   SimConnect adapter, and a spike test fails if anything under `bridge/` references the spike.
4. **Stopping always releases.** S or Esc, Ctrl+C, a crash, closing the console, or the sim
   quitting: every freeze and slew the run switched on is switched off again, and the player
   reads the sim's freeze/slew state back to confirm. If the process was killed hard (Task
   Manager) a freeze can stay on (closing the SimConnect connection does not undo it): run
   `follower-sim restore`.
5. **Ends on the ground.** A normal playback ends at the end of the recording, stopped on the
   ground. The player refuses a recording (or a `--from/--to` cut) that ends in the air or
   still moving, unless you pass `--allow-air-end`. The stop key always works, also in the
   air; then the aircraft is yours to fly from where it is (the report says so).
6. **Handover test** (`--handover-test`, B or C only): at the chosen point, which must be a
   steady taxi (on the ground, 5–40 kt), the player releases the freezes exactly as the stop
   key does, writes nothing more, logs 5 s and stops. **The aircraft is then yours and
   rolling at taxi speed: be ready to brake and steer.** The player says so before you type
   `OFFLINE`, and again at the release. Same offline rules as every playback.

## Build on the sim PC

Needs Windows x64 and the .NET 8 SDK. SimConnect comes from the MSFS 2024 SDK if
`MSFS2024_SDK` is set, otherwise from `bridge/third_party/simconnect/`, same as the bridge.

```
dotnet build spike/Follower/Follower.sln -c Release
dotnet test spike/Follower/Follower.Core.Tests
```

The programs end up in `spike/Follower/Follower.Sim/bin/x64/Release/net8.0/follower-sim.exe`
(sim) and `spike/Follower/Follower.Cli/bin/Release/net8.0/follower.exe` (offline tools).
Below, `follower-sim` and `follower` mean those two.

## Record

Fenix A320 at a gate, offline. Then:

```
follower-sim record circuit1.cxrec --note "ESSA 19R, light wind"
```

Fly a **full circuit**: about 20 s parked, taxi with at least one turn, takeoff, a circuit,
landing, rollout, taxi off and stop. Every playback then ends on the ground. Press S to stop
recording once the aircraft is stopped. The recorder warns if the recording does not end
stopped on the ground.

It records every sim frame of your aircraft: time (the PC's stopwatch, plus the sim's own
clocks `SIMULATION TIME` and `ABSOLUTE TIME`), lat/lon/alt, pitch/bank/heading, world and
body velocities, body rotation rates, on-ground, ground speed, height above ground, gear,
flaps handle, and the controls: yoke/sidestick, rudder pedals, elevator, aileron, rudder and
spoiler deflections, the Fenix sidestick vars (`N_FC_SIDESTICK_CAPT/FO_PITCH/BANK`) and
`G FORCE`. About 160 bytes per frame (10 KB/s at 60 fps). Check it:

```
follower inspect circuit1.cxrec
```

This prints:

- **The clocks.** For the stopwatch, `SIMULATION TIME` and `ABSOLUTE TIME`: frame step, steps
  under 3 ms, repeated frames, and the *speed error* (position change ÷ clock step against the
  recorded velocity, frame to frame). The smoothest clock is the one whose speed error is
  lowest; `--clock auto` (the default) picks it and drops frames where position and clock
  both repeat. Expect the sim clocks near 0 % and the stopwatch far higher.
- Frame times on that clock, the phases, and whether it ends on the ground.
- How well the recorded world velocities match the position changes over 0.5 s windows. Above
  5 % the velocity axes are not what the code assumes (X east, Y up, Z north): tell us. (On
  the first recording the old frame-to-frame check said 10 %; that was stopwatch noise, see
  [research §6.8](../research/yourcontrols.md#68-what-it-means-for-jerky-taxi-and-the-jumping-ground-speed-method-b---tangents-fd).)
- Steady taxi stretches, with a suggested `--handover-test` point for each.
- Min/max of every field.

Recordings made before 2026-11-04 have no sim clocks and no G force: they still play on the
stopwatch, but their timing noise is in the data, so record again. The min/max of the `fnx_stick_*` fields show the Fenix stick
range: pass the full deflection as `--fenix-stick-scale` when playing (the default 1 assumes
−1..1).

## Play

Load the Fenix (any parking spot: the player moves you to the start of the recording),
make sure you are offline, then for example:

```
follower-sim play circuit1.cxrec --method A --rate 30
```

The player shows where it will move you and what the method does, asks for `OFFLINE`,
holds the aircraft at the first frame for 3 s (`--settle`) so the sim settles, then plays.
On every sim frame it reads your aircraft back and writes where it should be. The PF side
is simulated: the recording is "sent" at 20 or 30 Hz, each frame gets a network delay
(default 35 ± 10 ms), and the follower plays it out of a buffer.

**Timing.** The player's clock is your sim's own clock (the one `inspect` chose for the
recording), so each sim frame moves the follower by exactly one sim frame's worth, whenever
the PC gets round to handling it. If several messages are waiting, it handles them all and
writes once, for the newest. A message whose sim time did not move (the same frame twice) is
not played. If the sim clock stops moving, the player falls back to the stopwatch for the rest
of the run and the report says so. `--clock sim|abs|stopwatch` forces a clock.

### The three methods

| | What it does | What to expect |
|---|---|---|
| **A** | Writes position, attitude, body velocities and rotation rates every frame (`--velocities body`). Nothing else. | The aircraft stays live: the sim's physics and the **Fenix fly-by-wire** run between our writes and may push back against the placed attitude (the FBW holds the attitude it thinks it should have). Watch the reaction columns. Ground contact may fight the written altitude. |
| **B** | The sim's freeze lat/lon, altitude and attitude, and every frame position, attitude and the velocity set YourControls and FS Copilot write (`--velocities mixed`, below). The player checks the freeze flags and sends a freeze again if one reads off (what both projects do). | The sim no longer moves the aircraft on its own, so only our writes move it. Risks known from YourControls: ground jitter, and what a role handover does to an aircraft that was frozen. |
| **C** | Slew mode on; writes position and attitude every frame (velocities mean nothing in slew). | No physics at all. Expect lost ground contact effects, gear/wheel and sound oddities, and whatever the Fenix does when slewed. |

`--velocities` chooses what is written with the position (methods A and B):

| Choice | Writes | Default for |
|---|---|---|
| `mixed` | `VELOCITY BODY Y/Z`, `VELOCITY WORLD X/Z`, `VERTICAL SPEED`, `G FORCE` (the set of both projects, research §6.1). The last two are written on their own: if the sim refuses one, only that one stops, and the report says so | B |
| `body` | `VELOCITY BODY X/Y/Z` and the body rotation rates | A |
| `world` | `VELOCITY WORLD X/Y/Z` and the body rotation rates | |
| `none` | position and attitude only | |

### Smoothing

- **Playout buffer** (`--buffer`, default 100 ms): the follower plays behind the newest
  frame it expects, so it almost always has a frame on both sides to interpolate between.
  The clock offset between PF and follower is the fastest arrival of the last 2 s, so jitter
  does not move the playback clock. When it has to move, it moves gradually: a sudden change
  in clock rate would be a sudden change in speed.
- **Interpolation:** position on a cubic curve through both frames, using their velocities
  (default); attitude as a rotation (no trouble at north); the rest linear. `--tangents fd`
  uses the neighbouring positions instead. It follows every bit of timing noise, so use it
  only to compare.
- **Late frames:** dead reckoning from the newest frame with its velocity and the
  acceleration and turn rate between the last two frames, at most `--extrapolate` (default
  250 ms). After that the follower **holds**, which in flight means it stops dead. When
  frames come again, the difference is faded out over `--blend` (default 200 ms).

### Simulated network

| Option | Default | |
|---|---|---|
| `--rate` | 30 | stream rate, Hz (20 and 30 are the ones to compare) |
| `--latency` / `--jitter` | 35 / 10 ms | delay = latency ± jitter, uniform (`--jitter-shape normal`: normal with σ = jitter) |
| `--loss` | 0 % | lost frames |
| `--burst` / `--burst-ms` | 0 % / 150 ms | chance per frame that the link stalls; everything sent during the stall arrives together when it ends |
| `--reorder` | off | let frames overtake each other. Off by default because the bridge streams over a WebSocket (TCP), which delivers in order |
| `--seed` | 1 | same seed, same delays: runs are repeatable |

### Handover test

```
follower-sim play circuit1.cxrec --method B --handover-test 60
```

Plays method B up to 60 s into the recording (it must be a steady taxi there; `inspect`
suggests points), then releases the freezes as the stop key does and only watches for 5 s.
The run folder gets a `handover.csv` (every frame after the release) and a section in
`report.md`: ground speed at the release, after 1 s and at the end, heading change, the
largest jump in one frame, the largest vertical step, and whether the aircraft left the
ground. The question: does the aircraft keep its speed and heading, or does it jump or stop?

### Run matrix

All on the same circuit recording. Fly nothing during a playback (hands off the stick, so the
reaction columns mean something).

| # | Command | Why |
|---|---|---|
| 1 | `play circuit1.cxrec --method A --rate 30` | baseline A |
| 2 | `play circuit1.cxrec --method B --rate 30` | baseline B (now with `mixed` velocities and the sim clock) |
| 2a | the same with `--velocities world`, then `--velocities none` | which velocity set steadies the ground speed display? |
| 2b | `play circuit1.cxrec --method B --handover-test <s>` | handover on the taxiway |
| 3 | `play circuit1.cxrec --method C --rate 30` | baseline C |
| 4–6 | the same with `--rate 20` | is 20 Hz enough? |
| 7 | best method, `--rate 30 --burst 1` | 150 ms stalls: bridged by dead reckoning? |
| 8 | best method, `--rate 30 --burst 1 --burst-ms 400` and again with `--extrapolate 600` | stalls longer than the limit |

Then `follower compare runs/*` gives one table. While playing, also note what you see from
the cockpit (shaking on the ground, attitude wobble, sounds, warnings, ECAM): the numbers do
not see everything.

## What we measure

Each run writes a folder `runs/<time>-sim-<method>-<rate>Hz-<latency>ms/` with `report.md`
(settings and one table per phase), `summary.json` (for `compare`) and `frames.csv` (one row
per sim frame), plus `handover.csv` for a handover test. All times, frame steps, jitter and
spikes are on the player's clock (the sim's own clock when available), so the PC's handling
delays no longer count as jitter. Phases come from the recording: **gate** (on the ground, below 1 kt), **taxi**
(on the ground, below 40 kt), **runway** (takeoff and landing roll), **flight**.

| Column | Meaning |
|---|---|
| pos, cross, vert | The follower as read back from the sim against the recording at the moment it should show, in metres. Cross-track and vertical are what you see out of the window; along-track is mostly timing. |
| att | Attitude error, degrees (angle between the two rotations). |
| att w/o reaction | The same without the frames where the aircraft reacted on its own (below). |
| cmd pos / cmd att | Read-back against what we wrote: how well the sim obeyed the method. For A this assumes the sim moves the aircraft on for one frame with its velocity; for B and C it should stay exactly where written. |
| jitter | \|follower acceleration − recording acceleration\| per frame, from second differences, m/s², and the same for rotation in °/s². This is the "is it smooth enough to sit in" number. |
| spikes | Frames with jitter above 2 m/s² or 20 °/s² (`--spike-accel`, `--spike-ang`), also per minute. |
| extrap / hold | Frames played from dead reckoning, and frames held after the limit. Plus the number of late stretches and the longest one. |
| reaction | Frames where the follower's elevator, aileron or rudder is more than 2° (`--surface-reaction`) from the recorded one **while the follower's own stick is neutral**: the aircraft (the Fenix FBW) reacting to what we placed, not our placement. Frames where the follower's stick is not neutral (someone touched it) are counted separately as "stick touched". |
| lag | How far behind the recording the follower plays (fastest network delay + buffer + a frame). |
| GS err | \|the follower's `GROUND VELOCITY` as read back − the recording's\|, kt (`gs_kt`, `truth_gs_kt` in `frames.csv`). |
| GS jump | How much more the follower's ground speed changed in one frame than the recording's, kt. "The ground speed jumps" as a number. |
| frame ms | Sim frame times during the run (player's clock). |
| PC ms | The same on the PC's stopwatch (`pc_ms`): shows the handling delays the sim clock removes. |

What "good" looks like still has to be agreed. A starting point: cross-track and vertical
p95 well under a metre, attitude p95 under 0.5°, jitter p99 under 1 m/s², no spikes at the
gate and on the taxiway, and no reaction frames that change the attitude.

## Offline (no sim)

The same pipeline runs on Linux or Windows against a perfect simulated sim, which is useful
for tuning buffer, extrapolation and blend before using the sim:

```
follower synth synth.cxrec                 # synthetic full circuit, 60 fps, ~10 min
follower simulate synth.cxrec --rate 20    # same options as play
follower compare runs/*
```

Offline the methods differ only in how the read-back is compared; the sim behaviour is what
the real runs are for. First offline results on the synthetic circuit (all phases):

| Run | pos p95 m | jitter p99 m/s² | spikes/min | extrap / hold frames |
|---|---|---|---|---|
| 30 Hz, 35 ± 10 ms | 0.00 | 0.05 | 0 | 0 / 0 |
| 20 Hz, 35 ± 10 ms | 0.00 | 0.41 | 0 | 0 / 0 |
| 30 Hz, stalls 1 % × 150 ms | 0.00 | 0.21 | 1.4 | 1158 / 0 |
| 30 Hz, stalls 1 % × 400 ms | 0.44 | 1387 | 257 | 2752 / 1069 |
| 30 Hz, stalls 1 % × 400 ms, `--extrapolate 600` | 0.00 | 0.45 | 6.8 | 3821 / 0 |

So with a perfect sim the buffer and interpolation are not the problem, at 20 Hz either.
Holding is: a stall longer than the extrapolation limit makes the follower stop dead and jump.
That is a reason to try a longer limit on the sim (run 8).

## Follower in YourControls and FS Copilot

Both ship our method B: the three freezes, plus position, attitude and a fixed set of
velocities written once every sim frame from a WASM module inside the sim. Both use a straight
line between points, a buffer (about 100 ms and 400 ms) and hold when data is late, and both
have no special handling for the ground or the handover. Our differences are where we write
(from outside the sim, through SimConnect; since 2026-11-04 timed on the sim's own clock) and
the smoothing (cubic curve, prediction, blend). Details, known problems and the changes to try next:
[research §6](../research/yourcontrols.md#6-follower-how-yourcontrols-and-fs-copilot-place-the-aircraft).

## Notes

- Angles are stored as SimConnect delivers them (MSFS pitch is negative nose up). Nothing
  depends on the signs: attitude is interpolated and compared as a rotation.
- The world velocity axes (X east, Y up, Z north) do matter for the default tangents; `inspect`
  checks them on every real recording.
- Reaction detection uses the generic yoke simvars and the Fenix sidestick L:vars. If the
  Fenix's surfaces do not show in the generic deflection simvars, the reaction columns stay
  empty. Check `inspect` on the first recording.
- All logic is in `Follower.Core` (cross-platform, unit tested); `Follower.Sim` is only the
  SimConnect host: connect, read the state, write the placement, send the allowed events.

## Send back

The `runs/` folders (all three files), the recording(s), and your notes from the cockpit per
run.
