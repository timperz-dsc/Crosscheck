# Fenix A320 web MCDU protocol

Observed on 2026-10-31 against Fenix Block 2 in MSFS 2024. Everything below comes from what the local web server at `http://localhost:8083/` serves: the HTML, the JS bundles, and live GraphQL responses. No Fenix binaries were inspected.

## Web app

- The server is ASP.NET Core (Kestrel). It serves an Angular single-page app called "FenixSim EFB".
- The MCDU page is at `/#/mcdu?cdu=1&fullscreen=false` (or `cdu=2`). It is lazy-loaded from the chunks `964.*.js` and `149.*.js` (`McduModule`). Without `cdu`, the page asks the user to pick Left or Right.

## Transport: GraphQL

- **HTTP:** `POST http://localhost:8083/graphql`. Use this for queries and mutations.
- **WebSocket:** `ws://localhost:8083/graphql`, subprotocol `graphql-transport-ws` (the `graphql-ws` library). The client sends `connection_init`, waits for `connection_ack`, then sends `subscribe`. Data arrives as `next` messages, and the client answers `ping` with `pong`.

The app reads everything through a generic "dataref" API:

```graphql
subscription DataRefsChange($names: [String!]!) { dataRefs(names: $names) { name value } }
query GetDataRef($name: String!) { dataRef { dataRef(name: $name) { value } } }
mutation WriteIntDataRef($name: String!, $value: Int!) { dataRef { writeInt(name: $name, value: $value) } }
```

There are also `writeFloat`, `writeBool` and `writeString` mutations. The subscription pushes `{name, value}` whenever a subscribed dataref changes.

## Screen data

- **Addressing:** Each CDU has its own dataref, `aircraft.mcdu1.display` or `aircraft.mcdu2.display`.
- **Format:** The screen arrives as text, not an image. The value is an XML string:

```xml
<root>
  <title>       MCDU MENU        </title>
  <line>s                        </line>
  <line>g&lt;FMGCw            CONFIG&gt;</line>
  ... 12 <line> elements in total ...
  <scratchpad>SELECT DESIRED SYSTEM   </scratchpad>
</root>
```

- **Grid:** 14 rows (title, 12 lines, scratchpad) of 24 characters each.
- **Modifiers:** Lowercase letters are not displayed. Each one changes the style of every character after it:
  - Colour: `a` amber, `w` white, `c` cyan, `g` green, `m` magenta, `r` red, `y` yellow.
  - Font size: `s` small, `l` large.
  - Displayed MCDU text is always uppercase, so stripping lowercase letters leaves the plain text.
- **Special glyphs:** A few characters stand for symbols in Fenix's `Mcdu` font. For example, `¢` and `£` appear where the real MCDU shows the page arrows. We do not have a full mapping yet.
- **Other datarefs:** The page also subscribes to the CDU annunciators `system.indicators.I_CDU{n}_FM1`, `_IND`, `_RDY`, `_FM2`, `_FAIL`, `_FM` and `_MCDU_MENU`, plus some lighting values.

## Key presses

- **Web page:** It presses keys with `WriteIntDataRef(name: "system.switches.S_CDU{n}_KEY_{KEY}", value: 1)`. It only ever writes 1.
- **Key names:**
  - Line select keys: `LSK1L`–`LSK6L`, `LSK1R`–`LSK6R`.
  - Page keys: `DIR PROG PERF INIT DATA FPLN RAD_NAV FUEL_PRED SEC_FPLN ATC_COM AIRPORT MENU`.
  - Arrows: `ARROW_LEFT ARROW_RIGHT ARROW_UP ARROW_DOWN`.
  - Brightness: `BRT DIM`.
  - Input: `0`–`9`, `A`–`Z`, `DOT MINUS SLASH SPACE OVFLY CLEAR`.
- **Through our own L: vars (what Crosscheck will use):** `L:S_CDU{n}_KEY_{KEY}`, written via SimConnect.
  - The press fires on the **0 → 1 edge**. The screen changed while the var was still 1.
  - Fenix does **not** reset the var. It stays at 1 until it is written back to 0, so the next press would have no edge.
  - A press is therefore: write 1, then write 0 about 100 ms later. That pattern gave 100 out of 100 successful presses in the latency test.

## Measured (McduReader, 2026-10-31)

| | n | min | median | p95 | max | fail | other CDU changed |
|---|---|---|---|---|---|---|---|
| CDU1 | 50 | 384.8 | 446.2 | 474.0 | 502.5 | 0 | 0 |
| CDU2 | 50 | 395.6 | 447.6 | 476.8 | 479.2 | 0 | 0 |

These are milliseconds from the L: write until the GraphQL feed delivers a screen whose text hash differs from the previous one. The keys were page keys only (MENU, DATA, FPLN, INIT, PROG), 10 rounds per CDU.

The first DATA press in the probe, which left MCDU MENU for the FMGC, took about 1460 ms.

The narrow spread around 450 ms suggests a fixed delay, either in the simulated FMGC or in how often the backend publishes the display. This spike cannot tell which. To find out, compare against the screen inside the sim, for example with a high-speed screen recording.
