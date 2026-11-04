# ui/

The Crosscheck UI: React + TypeScript + Vite. The bridge serves the built files on
`http://127.0.0.1:8790/` and shows them in its own window (WebView2). Any browser on the same
computer can open that address too, which helps when debugging. Everything comes from the
bridge's local API (`/api/...`, see [bridge/README.md](../bridge/README.md)); the UI keeps no
state of its own.

The look follows the design mockups in [docs/design/ui/](../docs/design/ui/). Geist and
Geist Mono come from the `geist` npm package and are bundled; the UI loads nothing from the
network.

## Build

```
cd ui
npm install
npm run build        # → ui/dist; the next bridge build copies it to wwwroot
```

## Develop

Start a bridge (for example `dotnet run --project bridge/Crosscheck.Bridge -- --sim fake`), then

```
npm run dev          # http://127.0.0.1:5173, API calls go to the bridge on 127.0.0.1:8790
```

`CROSSCHECK_BRIDGE=http://127.0.0.1:8792 npm run dev` points it at another bridge.

## Test

```
npm test             # component tests with mocked API data, plus a check of the visual rules
npm run e2e          # builds UI and bridge, starts the session server and two fake-sim bridges,
                     # and drives both pilots in headless Edge (needs npm install in server/)
```

`npm run e2e:panel` replays gate test 2 through the session panel (no bridge needed) and saves the
screenshots in [docs/design/ui/screens/](../docs/design/ui/screens/), in the light and the dark Windows theme.
It also checks that the window never scrolls, down to the minimum size of 900 × 600. On Linux (Codespaces)
use Playwright's Chromium instead of Edge: `npx playwright install chromium`, then
`CROSSCHECK_BROWSER=chromium npm run e2e:panel`.

`CROSSCHECK_SCREENSHOTS=<folder> npx playwright test` saves a screenshot of each screen, to
compare with the mockups.

## Layout

- `src/api.ts`: status polling (1 s), the events socket, the POST requests
- `src/App.tsx`: picks the screen from the bridge's `phase`
- `src/screens/`: Start, Waiting (code for the first officer), Preflight, Session
- `src/words.ts`: every sentence the UI shows
- `src/styles.css`: the design tokens (light and dark, following the Windows theme) and patterns; no other colours, no shadows, no gradients
