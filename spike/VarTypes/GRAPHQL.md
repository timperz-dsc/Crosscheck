# Fenix GraphQL backend: what it exposes

Observed on 2026-10-31 with Fenix Block 2 in MSFS 2024, aircraft powered at the gate. Sources: the EFB web app served on `localhost:8083` (HTML, JS bundles) and live queries. No binaries were inspected, and the only writes were the L: var writes Part A needed. All GraphQL calls were reads.

## Server and introspection

- **Server:** ASP.NET Core (Kestrel) with HotChocolate, at `http://localhost:8083/graphql` (HTTP) and `ws://localhost:8083/graphql` (`graphql-transport-ws`).
- **Introspection is disabled.** `__schema` returns `HC0046 "Introspection is not allowed for the current request."` Fenix turned it off on purpose, so we did not try to work around it.

Instead, the schema below was reconstructed from the 35 GraphQL operations in the EFB's JS bundles, and dataref names were found by probing.

## Operations the EFB uses

| area | operations |
|---|---|
| datarefs | `query GetDataRef(name)`, `subscription DataRefsChange(names: [String!]!)`, `mutation WriteIntDataRef / WriteFloatDataRef / WriteBoolDataRef / WriteStringDataRef(name, value)` |
| settings | `GetSettingsState`, `SettingsStateSubscription`, `SetSetting(s)`, `SetDefault(s)`, `GetSoftwareVersion` |
| EFB/cabin | `OnDocumentUpdated`, `OnEfbResponseMessageUpdated`, `VirtualPurserCommands`, announcements, boarding music (`fap.pram.*`) |
| Navigraph | `NavigraphAccessToken`, `NavigraphDisconnect` |

Everything about aircraft state goes through the generic dataref API. There are no typed queries for fuel, the flight plan and so on.

## Dataref namespaces

There is **no way to list datarefs.** No list query exists, and an unknown name returns `""` rather than an error, the same as an existing string that happens to be empty. Names have to be known or guessed. Sources for names:
- the strings in the EFB JS bundles (90 names),
- the L: var names in the profiles (`system.*` mirrors them),
- informed guesses.

| namespace | content | example |
|---|---|---|
| `system.switches.S_*` | switch state as Fenix sees it | `system.switches.S_OH_ELEC_BAT1` = 1 |
| `system.indicators.I_*` | annunciators/lights | `system.indicators.I_MIP_GEAR_1_L` = 1 |
| `system.numerical.N_*` | numeric values | `system.numerical.N_FCU_ALTITUDE` = 100 |
| `system.analog.A_*` | analog knobs, **scaled 0–1024** (the L: var is 0–1) | `system.analog.A_OH_PNEUMATIC_COCKPIT_TEMP` = 512 |
| `system.gates.B_*` | boolean system states | `system.gates.B_HYD_PARKING_BRAKE_SET` = true |
| `aircraft.*` | semantic aircraft data (below) | `aircraft.fuel.left.amount.kg` = 1500 |

These have **no** GraphQL mirror:
- `E_` encoders (tried under `system.encoder(s)`, `system.numerical`, `system.switches` and others),
- `_Cover` guards,
- `CB_` circuit breakers (`system.cb`, `system.breakers`, `system.circuitbreakers`).

**`system.switches` does not always use the L: var's numbers.** For the dome light, L: 0/1/2 is GraphQL 2/1/0. For pack flow, ND mode and ND zoom the numbers are the same. Pushbuttons are mirrored as pressed or not: master caution shows the L: counter's parity (odd = pressed).

## State that is not available as L: vars

Values are from the powered aircraft at the gate. `""` means "not found" (or empty).

| wanted | found | value now | not found (tried) |
|---|---|---|---|
| fuel per tank | `aircraft.fuel.left.amount.kg`, `.right.`, `.center.`, `.ACT1.`, `.ACT2.` and `.capacity` | 1500 / 1500 / 0 / 0 / 0 kg, left capacity 6264 | inner/outer split, fuel temperature, fuel used |
| FOB | `aircraft.fuel.total.amount.kg` (also `aircraft.fuel.total.amount`) | 3000 kg | `aircraft.fuel.fob`, EFOB |
| refuel | `aircraft.refuel.fuelTarget.kg`, `aircraft.refuel.refuelingPower` | | |
| weights/CG | `aircraft.weight.zfw`, `.empty`, `.lw.kg`, `.mlw.kg`, `.mtow.kg`, `.mzfw.kg`, `aircraft.cg` | ZFW 43830 kg, CG 25.86 % | GW, TOW |
| payload | `aircraft.passengers.zone1..4.capacity`, `aircraft.passengers.seatOccupation`, `aircraft.cargo.forward/aft/bulk.amount` + `.capacity` | | |
| FMGS init | `aircraft.fms.init.block`, `.zfw`, `.zfwcg`, `aircraft.fms.origin` | empty (no flight plan entered) | flight plan, destination, cruise level, cost index, flight number |
| FMGS perf | `aircraft.fms.perf.takeOff.v1`, `.v2`, `.vr`, `.flaps`, `.flexTemp`, `.ths`, `.shift` | 0 (not entered) | predictions |
| MCDU screens | `aircraft.mcdu1.display`, `aircraft.mcdu2.display` (see spike/McduReader) | text | |
| ECAM | the SD page shows only through the ECAM button lights `system.indicators.I_ECAM_*` (also L:I_ECAM_*) | | `aircraft.ecam.page`, `.sd.page`, `.lowerPage`, `.messages`, `aircraft.ewd.memo` |
| FCU values | `system.numerical.N_FCU_ALTITUDE/HEADING/SPEED/VS` (also L:N_FCU_*) | 100 ft / 0° / 100 kt / 0 | `aircraft.fcu.*` |
| hydraulics | `aircraft.hydraulics.green/blue/yellow.pressure`, `aircraft.hydraulics.green.quantity` | 2.65 / 2.65 / 2.65, quantity 14 | |
| electrical | `aircraft.electrical.battery1.voltage` | 26.9 V | AC/DC bus states, ext power (`system.gates.B_ELEC_BUS_POWER_DC_SERVICE` = true exists) |
| pneumatic/air | `aircraft.cabin.pressure`, `aircraft.cabin.fwd/aft.temp.air/selected/offset`, `aircraft.acAndPress.zoneController.primaryOperational`, `aircraft.airConditioning.lavGalleyExtractionFan.running` | 14.57 psi, fwd air 6.1 °C, fwd selected 24 | bleed pressures, cabin altitude, cockpit zone temperature |
| APU/engines | `aircraft.apu.running` = false; `aircraft.engines.1.n1` **exists but errors**: "Any cannot coerce the runtime value of type System.Double" | | APU N/EGT |
| position | `aircraft.latitude/longitude`, `aircraft.altitude`, `.aboveGround`, `aircraft.heading.magnetic/true`, `aircraft.speed.ground`, `aircraft.magnetic_variation` | | indicated airspeed and altitude |
| cabin systems | about 20 `aircraft.communication.cids.*` (doors, slides, water and waste tanks, FAP) | | |
| failures | none found | | `aircraft.failures`, `aircraft.failure.active` |

In short, GraphQL adds per-tank fuel and FOB, weights and CG, payload, FMGS init and take-off perf, hydraulic pressures, battery voltage, cabin temperatures and pressure, and both MCDU screens. We found no ECAM messages, no SD page dataref beyond the button lights, no flight plan and no failures.

## Subscriptions and update rate

- **Many datarefs per subscription: yes.** `DataRefsChange(names: [...])` takes a list (22 were tested). Each update arrives as its own `next` message with `{name, value}`.
- **Initial snapshot:** the first values arrive about 1.6 s after `subscribe`, every time.
- **Live changes only for some datarefs.** After the snapshot, the subscription pushes changes only for a subset:
  - **Pushed:** `system.switches.S_OH_INT_LT_DOME`, `aircraft.mcdu1/2.display`, `aircraft.heading.magnetic`, `aircraft.altitude`, `aircraft.cabin.fwd.temp.air`. These are mostly datarefs the EFB itself uses.
  - **Never pushed, snapshot only:** `system.switches.S_OH_PNEUMATIC_PACK_FLOW`, `S_FCU_EFIS1_ND_MODE/ZOOM`, `system.numerical.N_FCU_*`, `system.analog.*`, `system.indicators.I_ECAM_*`, `system.switches.S_MIP_MASTER_CAUTION_CAPT`. This held even with a single dataref in its own subscription.
  - In the VarTypes suite the subscription delivered 0 of 330 changes, while HTTP polling saw all of them.
- **Push latency:** the dome light took 500–1000 ms (it looks like a slow internal poll). MCDU screens took about 450 ms, the same as HTTP. Static aircraft values pushed about 2 times in 15 s.
- **HTTP polling works for every dataref.** Polling one name in a loop sees a change after a median of **64–76 ms** across all types, with the first query taking about 1.5 s to warm up. That makes GraphQL HTTP faster than the effect L: vars (105–120 ms), probably because Fenix updates its internal state first and the L: vars one or two frames later.

### Consequences for Crosscheck

- **Use the subscription only where it is known to push.** That means MCDU screens and EFB data. Never treat a silent subscription as "unchanged".
- **For verifying Fenix state, poll over HTTP**, for example a small batch of names after each write, or use the effect L: vars through SimConnect.
- **Confirm the dataref API first.** There is no list API, introspection is off, and names are undocumented, so every dataref a profile relies on has to be confirmed once against a live aircraft.
