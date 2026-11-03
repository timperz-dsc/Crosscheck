# CRO-34 – profile changes for the calm session panel

New profile hash: `sha256:04cde088101a15556452d0dfbbd20ed0c5f405f276d335f5291bf16d397181fe`

Made with `tools/profile-gen` from `overrides.yaml` and the hand-written part of `profile.yaml`; the
generated block was not edited by hand. Proof that only the intended vars changed:
`dotnet run --project tools/profile-gen -- diff <profile at ec139f4> profiles/fenix-a320/profile.yaml`.

**Result:** added 2, removed 14, changed 219. Every change is in one of the kinds below; nothing else moved.
The diff tool compares vars, datarefs and roles. The new top-level maps (`controls`, `group_priority`,
`outside_groups`) are in the hand-written part. `PanelProfileTests` pins all of it.

| Change | Vars | What |
|---|---|---|
| `tolerance` | 4 | `N_ELEC_VOLT_BAT_1/2` ±0.5 V; `N_HYD_PRESSURE_BRAKE_LEFT/RIGHT` ±0.3 (10 % of the 0–3 ×1000 psi gauge, owner decision) |
| `powered` | 86 | FCU/EFIS lamps and flags gated by `B_FCU_POWER`, RMP flags by `B_PED_RMPn_POWER` (the flags CRO-33 already used) |
| `control` | 63 | RMP inner/outer knob and displays → `rmpN_freq`; EFIS ND filter buttons and lamps → `efisN_nd_filter`; ECAM page keys and lamps → `ecam_page`; FCU managed/dashed lamps → their knob |
| `upstream` | 6 | FCU managed/dashed lamps wait for both FD buttons (an FD difference un-dashes the windows) |
| `priority` | 31 | 28 lights and lighting knobs on priority-2/3 panels → 5; ENG MASTER 1/2 and ENG MODE → 2 |
| `label` | 135 | "EFIS1 LS" → "LS (Capt)", "RMP1" → "RMP 1", FCU HDG/SPD/V/S/ALT, APU AUTO EXTING RESET |
| `class` → ignore | 6 | Windows are each pilot's own (owner decision CRO-36): `S_WINDOW_CAPT_LEVER`, `S_WINDOW_FO_LEVER`, `S_WINDOW_FO_LATCH`, `N_WINDOW_CAPT/FO`; `N_FC_RUDDER_TRIM_DECIMAL` (gate test 1 §4 list, until the stream) |
| added (ignore) | 2 | `S_WINDOW_CAPT/FO_LEVER_BUTTON` (were on the review list) |
| removed | 14 | Seats, armrests, jumpseat, tray tables, sunshades: from `ignore` to the review list ("syncs later", CRO-36): amber "Not synced yet" when moved, until they sync. Their `N_SEAT_*` readouts stay ignored |

Not changed on purpose: oxygen mask hatch and gravity gear (review list), cockpit door (verified),
window blinds (verified).

Still open:
- Hydraulic system pressures are not compared at all (GraphQL only, no profile entry): no tolerance to set.
- Other indicators have no proven power flag yet; they stay ungated until an observe run shows one.
- The gate test 1 §4 ignore list was otherwise already done (fuel gauges, brightness, flight-control
  positions, clocks).
