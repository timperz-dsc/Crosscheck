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
```

`match` needs at least one of `titles` or `packages`. Group names are lowercase (`a-z`, `0-9`, `_`). Every `group:` in `vars` and `datarefs` must be listed in `groups`.

## Vars

Common fields: `name`, `class`, `group`, optional `note`.

| class | write | required | optional |
|---|---|---|---|
| verified | absolute value | `values` or `range`, and `verify` where an effect source exists | |
| delta | the change only | `display` (a var in this profile) | `verify` |
| pulse | 1, then 0 | `pulse_ms` | `press: counter`, `verify` |
| analog | absolute value | `range` | `verify` |
| stream | none (PF streams it) | | `rate_hz` |
| owned | once at session start | | `verify` |
| observe | never | | |
| ignore | never | | |

- `values: [0, 1, 2]` is the allowed set. `range: [0, 1]` is min and max. Give one, not both. Both are hard limits: Crosscheck never writes outside them.
- `pulse_ms`: time between writing 1 and writing 0 (20 to 2000). `press: counter` is for master caution/warning, where Fenix counts press and release (odd = pressed).
- `display`: for an encoder (`E_*`), the var that shows the result (`N_*`). A corrective delta is sent when it differs between the sims.
- `observe` and `ignore` take no write or verify fields.

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

`expect: same` (default) means the effect source is compared between the sims, never against a fixed value. `expect: parity` compares odd/even only (press counters). `scale` is the factor between the written value and the source, for example 1024 for analog knobs in GraphQL.

## Datarefs

Fenix cannot list its GraphQL datarefs, so every one is written down by hand and must be confirmed once against a live aircraft. Use only names that are documented in `spike/VarTypes/GRAPHQL.md`.

```yaml
datarefs:
  - { path: aircraft.fuel.left.amount.kg, class: owned, group: systems, poll_hz: 1 }
```

`class` is `owned`, `observe` or `ignore`. They are polled over HTTP at `poll_hz`. Group `systems` is meant for these system values (fuel, hydraulics, electrics).

## Roles

Optional. Vars that mean something to Crosscheck itself, on top of being synced. Every var named here must also be listed in `vars`.

```yaml
roles:
  takeover: { captain: S_FC_CAPT_INST_DISCONNECT, fo: S_FC_FO_INST_DISCONNECT }
```

- `takeover`: the sidestick takeover pushbutton of each seat (class `verified` or `pulse`). When the pilot monitoring presses the button on their own seat, the bridge sends "I have control". The press is synced like any other var. Which seat a pilot sits in is set with the bridge option `--seat-side` (default: host = captain, guest = fo).

## Exclude

Glob patterns (`*` any characters, `?` one character, case-sensitive) for scanned vars that are not part of the aircraft at all: GSX `FSDT_*`, Chaseplane `p42_*`, other aircraft, mods, docking guidance. Use them to filter a scan. Vars that belong to the aircraft but are not synced (animations, cosmetics) are not excluded; they get class `ignore`. A var listed in `vars` that matches an exclude pattern is an error.

## Validate

```
dotnet run --project bridge/Crosscheck.Profiles.Cli -- validate profiles/fenix-a320/profile.yaml
```

Prints errors with line numbers, then a count per class and group. Exit code 1 if there are errors.
