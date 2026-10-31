# Fenix A320 profiles compared

Both files are read where they are and are not copied into this repo, because their licence is unknown.

- **v1.1:** `Downloads\Yourcontrols-FenixSim A320 v1.1_8ckST\FenixSim A320 v1.1.yaml`, by 73Whiskey.
- **YC:** `Documents\YourControls\definitions\aircraft\Fenix Simulations - Airbus A320ceo.yaml`, the definition YourControls ships ("Version 2.4.0.3311").

## Method

Names were extracted with the regex `L:[A-Za-z][A-Za-z0-9_]*`, which also picks up names inside RPN calculator code. "Active" means the name appears on a line that is not commented out.

YC also contains 823 names in commented-out entries (`# var_name: ...`). They are counted separately, because YourControls ignores them.

## Counts

| | v1.1 | YC |
|---|---|---|
| unique L: names, active | 1227 | 1592 |
| only in commented-out entries | 0 | 753 |

| active, by prefix | S_ | E_ | A_ | N_ | I_ | CB_ | other |
|---|---|---|---|---|---|---|---|
| v1.1 | 732 | 16 | 81 | 45 | 36 | 316 | 1 |
| YC | 838 | 16 | 98 | 69 | 0 | 320 | 251 |

Comparing the active names:
- 1144 appear in both files.
- 83 appear only in v1.1.
- 448 appear only in YC.

## Only in YC (active, 448)

| prefix | count | what they are |
|---|---|---|
| S_ | 127 | 59 `S_EXT_*` (cargo/passenger door handles, cargo lights, ground service panels), 47 `S_ASP4_*` (a 4th audio control panel, the observer seat), plus about 20 cockpit controls missing from v1.1: `S_FC_CAPT/FO_PRIORITY`, `S_FC_THROTTLE_*_MAX_REVERSE`, `S_OH_ENG_MANSTART_1/2_L`, `S_OH_AFT_FADEC_GND_1/2_Cover`, `S_OH_PNEUMATIC_ECON_FLOW`, `S_OH_PNEUMATIC_CARGO_FWD_ISOL_VALVE`, `S_OH_FUEL_ACT`, `S_OH_CALLS_EXIT/MID`, `S_CDU1/2_KEY_CLEAR_LINE`, `S_CDU2_KEY_CLB`, `S_ASP*_RAD` |
| E_ | 0 | |
| A_ | 17 | 16 `A_ASP4_*_VOLUME` (4th audio panel) and `A_EXT_CARGO_DOOR_BULK_HANDLE` |
| N_ | 39 | `N_FCU_ALTITUDE/HEADING/SPEED/VS` (the FCU selected values, used as read-back in TYPES.md), `N_MISC_PERF_TO_V1/V2/VR/FLEX`, `N_OH_ADIRS_*`, `N_FUEL_PRESEL`, standby-instrument bug markers, `N_CONFIG_*` (aircraft options), DCDU/standby brightness |
| I_ | 0 | YC has 459 I_ names, but all of them are commented out |
| CB_ | 14 | mostly **spelling variants** of v1.1 names: `CB_J18_CFDIU` vs v1.1 `CB_J18_CDFIU`, `CB_E04_DU_PFD_CAPT_SWTG` vs `..._FPD_...`, `CB_Z14_PROBES_3_STATIC` vs `PROBE_3`, and swapped `CB_N39/N42_ENG*_OIL_CHAN*`. Genuinely new: ADIRS 3 switching, cargo lights, refuel/defuel valve, THS actuator |
| other | 251 | 181 `FSDT_GSX_*` (the GSX ground-services add-on, not Fenix), 57 `B_*` (43 `B_CONFIG_*` aircraft/EFB options, door, lighting and cabin states), `FNX_TAIL_HI/LO`, `FNX_SELCAL`, `FNX_FLT_LOADED`, a few non-Fenix names (`A320_Ground_Power_Anim`, `SIAI_Pushback_Active`), and 5 parse artefacts from RPN code |

## Only in v1.1 (active, 83)

- The 36 `I_*` indicators (FCU mode lights, gear lights, master caution/warning and others). YC only has these commented out.
- 10 `CB_*` names, mostly the other half of the spelling pairs above.
- `N_DISPLAY_BRIGHTNESS_*`, `N_FCU_LIGHTING`, brake pressures, tillers and elevator trim.
- A few S_ names: `S_CDU1/2_BRIGHTNESS_UP/DOWN`, `S_DCDU1/2_BRIGHTNESS`, `S_STANDBY_DDRMI_*`, `S_XPDR_*`, `S_PED_ATC_8/9`, `S_FC_THR_INST_DISCONNECT_1/2`.

## A319/A321 duplicates?

No. Not a single name in either file contains 319 or 321, and the YC-only names are not per-variant copies of existing controls. The difference falls into these groups:
- **Genuinely new cockpit controls:** about 20 S_ names, plus the FCU, perf and ADIRS N_ values.
- **Exterior and ground handling:** S_EXT, the cargo CBs.
- **A 4th audio panel:** ASP4.
- **Aircraft configuration options:** `B_CONFIG_*`, `N_CONFIG_*`.
- **Third-party integration:** GSX.
- **Spelling variants:** in each CB_ pair at most one name can be the real one. An L: var that doesn't exist reads as 0, so this needs a separate check, for example toggling the breaker in the cockpit and watching both names.

For Crosscheck, a profile should start from v1.1 and add these from YC:
- The genuinely new cockpit S_ controls.
- `N_FCU_*` as read-back.
- `S_EXT_*`, if exterior doors are to be synced.

The GSX, ASP4 and `B_CONFIG_*` names are probably not worth syncing.
