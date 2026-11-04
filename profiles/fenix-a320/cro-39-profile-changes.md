# CRO-39 – readable labels

New profile hash: `sha256:cf10f5725d325b412396a1578d4aaf9b7b94b1805d9ce3d1031cad3c06e88710`

Only labels changed. The generated labels come from a new rule in `tools/profile-gen` (`Labels.cs`);
the generated block was not edited by hand. Proof:
`dotnet run --project tools/profile-gen -- diff <profile at 2575949> profiles/fenix-a320/profile.yaml`.

```
added 0, removed 0, changed 1451
profile hash: sha256:cf10f5725d325b412396a1578d4aaf9b7b94b1805d9ce3d1031cad3c06e88710
```

All 1451 `~` lines change `label` and nothing else (checked with a script), and `git diff` of
`profile.yaml` touches only `label:` lines. The full list, old and new, is in
[cro-39-labels.csv](cro-39-labels.csv) (1303 of them are not `ignore`). `unsynced.csv` follows the same rule.

## The rule

- Panel prefixes go: OH, MIP, PED, FC, EXT (FCU as before). "EXT LT BEACON" becomes "Beacon light".
- Normal case. Real abbreviations stay uppercase (FCU, ECAM, MCDU, RMP, ADIRS, APU, IDG, ND, PFD, ELAC,
  VHF, …); a few short forms are written out (LT light, TEMP temperature, ENG engine, WR weather radar,
  FLT CTL flight control, …). MCDU, DCDU and ECAM keys keep the legend printed on the key.
- Sides and positions go into one parenthesis at the end: "(Capt)", "(F/O)", "(left)", "(aft)",
  combined as "(Capt, lower)". EFIS 1/2, CDU 1/2 and DCDU 1/2 are the captain's and the first officer's.
- On `I_` lamps a final `L` / `U` is the lower / upper legend of the button. Anywhere else `L` / `R`
  mean left / right. One exception: after ECAM, `L` / `U` is the lower / upper ECAM display
  (`A_DISPLAY_BRIGHTNESS_ECAM_L` → "Display brightness ECAM (lower)").

Pinned by `tools/profile-gen.Tests` (LabelTests).

## Examples

| Var | Before | After |
|---|---|---|
| `S_FC_RUDDER_TRIM_RESET` | FC RUDDER TRIM RESET | Rudder trim reset |
| `S_EXT_CARGO_BAY_AFT_LT` | EXT CARGO BAY AFT LT | Cargo bay light (aft) |
| `I_OH_ELEC_IDG1_U` | IDG1 light (upper) | IDG 1 light (upper) |
| `I_OH_PNEUMATIC_ENG1_BLEED_L` | ENG1 BLEED light (lower) | Engine 1 bleed light (lower) |
| `S_OH_EXT_LT_LANDING_L` | EXT LT LANDING L | Landing light (left) |
| `I_MIP_MASTER_CAUTION_CAPT_L` | MASTER CAUTION CAPT light (lower) | Master caution light (Capt, lower) |
| `S_CDU2_KEY_FPLN` | CDU 2 FPLN | MCDU F-PLN (F/O) |
| `I_OH_FLT_CTL_ELAC_1_U` | CTL ELAC 1 light (upper) | ELAC 1 light (upper) |
| `A_WR_TILT` | WR TILT | Weather radar tilt |
| `A_DISPLAY_BRIGHTNESS_ECAM_L` | DISPLAY BRIGHTNESS ECAM L | Display brightness ECAM (lower) |

## For review

- `S_OH_ENG_MANSTART_1_L` / `_2_L` are switches (not lamps) that end in `L`, so the rule makes them
  "Manual start 1 (left)". If that `L` means something else, an override `label:` fixes it.
- Hand-written labels moved to the same style: APU master, APU master light, FCU power,
  MCDU F-PLN (Capt), ND mode (Capt), and the override "APU auto exting reset". FCU ALT, FCU HDG,
  FCU SPD and FCU V/S already follow it (abbreviations) and stay.
