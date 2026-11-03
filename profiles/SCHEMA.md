# Profile format (version 1)

One YAML file per aircraft, in `profiles/<aircraft>/profile.yaml`. No inheritance, no includes. Add this first line to get validation in your editor:

```yaml
# yaml-language-server: $schema=../schema/profile.schema.json
```

Two checks exist. The JSON Schema ([schema/profile.schema.json](schema/profile.schema.json)) checks structure: types, allowed keys, enums. The validator in `bridge/Crosscheck.Profiles` also checks class rules and cross references, and reports file line numbers. The bridge refuses a file whose `format` it does not know.

## Top level

```yaml
format: 1                       # version of this format
aircraft:
  id: fenix-a320
  name: Fenix A320
  match:                        # how the bridge recognises the loaded aircraft
    titles: ["..."]             # globs against the sim's aircraft title
    packages: [fnx-aircraft-320]
  builds: ["3.0.0.7613"]        # builds this profile was made and tested for
groups: [overhead, fcu, ...]    # panel groups: snapshot hashing and ownership
exclude: ["FSDT_*", "p42_*"]    # scan filter, see below
vars: [...]                     # L: variables
datarefs: [...]                 # Fenix GraphQL datarefs
roles: {...}                    # vars with a special meaning, optional
group_labels: {...}             # names of the groups in the UI, optional
controls: {...}                 # controls made of several vars, with their names (CRO-34), optional
group_priority: {...}           # order of the red lines per group, optional
outside_groups: [...]           # walkaround groups: never red, optional
```

`match` needs at least one of `titles` or `packages`. Group names are lowercase (`a-z`, `0-9`, `_`). Every `group:` in `vars` and `datarefs` must be listed in `groups`.

## Vars

Common fields: `name`, `class`, `group`, optional `note`, `label`, `value_labels`.

| class | write | required | optional |
|---|---|---|---|
| verified | absolute value | `values` or `range`, and `verify` where an effect source exists | |
| delta | the change only | | `display` (a var in this profile), `verify` |
| pulse | 1, then 0 (`press: momentary`), or each press and release as it happens (`press: edge`) | `pulse_ms` (momentary only) | `press`, `verify` |
| analog | absolute value | `range` | `verify` |
| stream | none (PF streams it) | | `rate_hz` |
| owned | once at session start | | `verify` |
| observe | never | | |
| ignore | never | | |

- `values: [0, 1, 2]` is the allowed set. `range: [0, 1]` is min and max. Give one, not both. Both are hard limits: Crosscheck never writes outside them.
- `press`: `momentary` (default) writes 1, then 0 after `pulse_ms` (20 to 2000). `edge` is for Fenix push buttons, which count +1 on press and +1 on release (odd = down, `profiles/fenix-a320/observe-report.md`). Each down and up edge is sent on its own; the receiver writes its own value + 1, so a hold is mirrored for as long as it lasts. `edge` takes no `pulse_ms`. With `verify.lvar`, the lamp is also the button's state: a lamp that differs between the sims for 2 s is a red `toggle_mismatch` (never pressed automatically).
- `rest` (verified only, spring-loaded switches such as RUD TRIM): the position the switch returns to by itself, one of `values`. Every change is mirrored, so a hold lasts as long on both sides. If the release of a position Crosscheck wrote never arrives (30 s, link lost), Crosscheck writes `rest` back, with a red desync (protocol.md section 5).
- `alarm` (observe only): `{ text: "...", as: VAR }`. The indicator shows a pilot input Crosscheck cannot sync yet (for example the FCU managed lamps while one knob direction is invisible). A difference between the sims is red after 2 s, never quiet, with `text`; `as` (optional) groups several indicators of one control under one line. Never written.
- `display`: for an encoder (`E_*`), the var that shows the result (`N_*`). It is compared between the sims; an encoder is **never corrected automatically**. A difference that still exists 2 s after the last click is one red line ("turn the knob in the cockpit that is wrong"). Without `display` the encoder is verified by read-back only: the bridge confirms its own L: var took the change, and the encoder is not compared between the sims (its counter values differ per sim).
- `compare_when` (delta with `display` only): `{ powered: VAR, same: [VAR, ...] }`. The display is compared only while `powered` reads non-zero in both sims and every `same` var (mode lamps, the selected radio of an RMP) shows the same value on both sides. All of them must be `observe` vars. Otherwise the display is "not comparable" and never raises a line.
- `never_write: true` (verified or pulse): the **deny list** for irreversible controls (IDG disconnect, fire push buttons, every AGENT/DISCH button, RAT MAN ON, passenger oxygen MAN ON). Crosscheck never writes the var on any path (command, correction, "Set both", "Press here only", "Release it", safety release); the guard refuses it. A press is not mirrored: both pilots get a red line "IDG 1 pressed in the host cockpit – not mirrored (irreversible)". The raw value is still compared in snapshots (class `denied`), so a pilot who joins later sees the same line; it clears when both sims show the same value. `values`/`range` are optional; `rest` is not allowed. Covers of these buttons keep syncing.
- `observe` and `ignore` take no write or verify fields.

### The session panel (CRO-34)

The panel shows one line per control. A lamp is shown under the button that names it in `verify.lvar`,
a display under its knob (`display`), and a `B_` mode flag under the one encoder whose `compare_when.same`
lists it; none of that needs writing down. For the rest:

- `control: VAR_OR_ID` (any class but ignore and stream): show this var under that control: another var of
  this profile (no chains) or an id from the top-level `controls:` map (`rmp1_freq: RMP 1 frequency`).
- `tolerance: 0.5` (observe only, not with `alarm`): the sims match while the values differ by no more than
  this, in the var's units. Compared value by value outside the exact group hash (protocol.md section 7).
- `powered: VAR` (observe only): compared only while this flag reads non-zero in both sims. `VAR` is a plain
  `observe` flag (no tolerance or power gate of its own).
- `upstream: [VAR, ...]`: controls that change this one by themselves (both FDs off un-dash the FCU
  windows). While one of them differs, this difference is that line's effect, never a line of its own.
- `priority: 2..5`: the order of the red lines, instead of the group's. Top level: `group_priority:
  { fcu: 2, overhead: 3, pedestal: 4, cabin: 5 }` (safety lines are always 1), and `outside_groups: [ground]`
  for walkaround items (one amber "outside" line, never red).

### verify

```yaml
verify: { lvar: I_OH_ELEC_APU_MASTER_L, settle_ms: 150 }
verify: { mcdu: 1, settle_ms: 600 }
verify: { graphql: system.analog.A_OH_PNEUMATIC_COCKPIT_TEMP, scale: 1024, settle_ms: 100 }
```

Exactly one source, plus `settle_ms` (wait before comparing):

- `lvar`: another var in this profile that shows the effect.
- `mcdu: 1|2`: the screen hash of that CDU must be equal on both sims. There is no expected text, because the product cannot know what a screen should show. `contains: "TEXT"` is optional and for local tests only.
- `graphql`: `system.<switches|indicators|numerical|analog>.<NAME>` where `NAME` is a var in this profile, or any path listed in `datarefs`.

`expect: same` (default) means the effect source is compared between the sims, never against a fixed value. `expect: parity` compares odd/even only. `scale` is the factor between the written value and the source, for example 1024 for analog knobs in GraphQL.

## Datarefs

Fenix cannot list its GraphQL datarefs, so every one is written down by hand and must be confirmed once against a live aircraft. Use only names that are documented in `spike/VarTypes/GRAPHQL.md`.

```yaml
datarefs:
  - { path: aircraft.fuel.left.amount.kg, class: owned, group: systems, poll_hz: 1 }
```

`class` is `owned`, `observe` or `ignore`. They are polled over HTTP at `poll_hz`. Group `systems` is meant for these system values (fuel, hydraulics, electrics).

## Labels

Optional names for the UI. Without them the UI shows the var name and the number.

```yaml
group_labels: { mip: Main panel, cb: Circuit breakers }
vars:
  - name: S_OH_ELEC_APU_MASTER
    label: APU MASTER
    value_labels: { "0": OFF, "1": ON }    # keys are numbers in values/range
datarefs:
  - { path: aircraft.fuel.left.amount.kg, class: owned, group: systems, poll_hz: 1, label: Left tank fuel, unit: kg, drift_warn: 100 }
```

`group_labels` keys must be listed in `groups`. `unit` is shown with owned drift ("12 kg apart").
`drift_warn` (owned only): while the values differ by more than this, the UI shows one amber
line asking both pilots to load the same values. `drift_step` (owned only, optional): a change of
the difference smaller than this is sent at most every 10 s; larger changes at once. Default: half
of `drift_warn` (50 kg for fuel). Owned values are never written by Crosscheck.

## Not synced yet: `unsynced.csv`

Next to `profile.yaml`, generated by profile-gen from the review list: `name,label` for every
S_/A_ var that is not in the profile yet. The bridge watches these read-only. When a pilot moves
one, both pilots see one amber line "You moved <label> – not synced yet". They are never written.
The file is not part of the profile hash.

## Roles

Optional. Vars that mean something to Crosscheck itself, on top of being synced. Every var named here must also be listed in `vars`.

```yaml
roles:
  takeover: { captain: S_FC_CAPT_INST_DISCONNECT, fo: S_FC_FO_INST_DISCONNECT }
```

- `takeover`: the sidestick takeover pushbutton of each seat (class `verified` or `pulse`). When the pilot monitoring presses it, the bridge sends "I have control" and holds the press back; it is mirrored only once control is granted, as a press of the new pilot flying. A takeover press is never written into the sim of the pilot who is flying at that moment. Which seat a pilot sits in is set with the bridge option `--seat-side` (default: host = captain, guest = fo).

## Exclude

Glob patterns (`*` any characters, `?` one character, case-sensitive) for scanned vars that are not part of the aircraft at all: GSX `FSDT_*`, Chaseplane `p42_*`, other aircraft, mods, docking guidance. Use them to filter a scan. Vars that belong to the aircraft but are not synced (animations, cosmetics) are not excluded; they get class `ignore`. A var listed in `vars` that matches an exclude pattern is an error.

## Validate

```
dotnet run --project bridge/Crosscheck.Profiles.Cli -- validate profiles/fenix-a320/profile.yaml
```

Prints errors with line numbers, then a count per class and group. Exit code 1 if there are errors.
