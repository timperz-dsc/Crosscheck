# CRO-40 – profile changes after gate test 3

New profile hash: `sha256:7a7a83062a566c13f6767bb2e011791555fddb0bb8716b866944a5ea0a9d5700`

Made with `tools/profile-gen` from `overrides.yaml` and the hand-written part of `profile.yaml`; the
generated block was not edited by hand. Proof that only the intended vars changed:
`dotnet run --project tools/profile-gen -- diff <profile at 0dcce7b> profiles/fenix-a320/profile.yaml`.

```
added 0, removed 0, changed 3
~ A_FC_ELEVATOR_TRIM: class analog -> ignore; range [0,1] -> -
~ N_FC_RUDDER_TRIM_DECIMAL: class ignore -> observe; notice - -> Rudder trim differs – press RESET or adjust it.; tolerance - -> 1
~ S_EXT_CARGO_DOOR_SELECTOR_AFT: values [0] -> [0,1]
profile hash: sha256:7a7a83062a566c13f6767bb2e011791555fddb0bb8716b866944a5ea0a9d5700
```

| Change | Var | Why |
|---|---|---|
| analog → `ignore` | `A_FC_ELEVATOR_TRIM` | Gate test 3 section 2B: the host's trim wheel sent 100 … 5400, outside the guessed range [0, 1]. Owner decision (gate test 3 section 8): pitch trim stays ignore until the stream (CRO-13), like `N_FC_ELEVATOR_TRIM`. |
| ignore → `observe`, `tolerance: 1`, `notice` | `N_FC_RUDDER_TRIM_DECIMAL` | Owner decision (gate test 3 section 8): the rudder trim position is one amber line with a tolerance of one display step, "Rudder trim differs – press RESET or adjust it.". It was the one silent difference in gate test 3 (section 4.1). Never written. |
| `values: [0]` → `[0, 1]` | `S_EXT_CARGO_DOOR_SELECTOR_AFT` | Gate test 3 section 2C: the guest moved it to 1 and the host's guard refused it; same values as its FWD twin. |

Note-only changes (the diff tool leaves notes out; checked with `git diff`): every note that called the stream
CRO-17 now says CRO-13 (CRO-17 is the follower spike): the four throttle axes and nine flight-control positions,
13 notes in all. Plus the notes of the three vars above (16 changed notes).

`notice` is a new profile field (observe only, never with `alarm`; schema, `profiles/SCHEMA.md`, profile-gen).
`PanelProfileTests` pins all of it.
