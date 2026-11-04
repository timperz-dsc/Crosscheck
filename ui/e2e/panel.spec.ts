import { readFileSync, mkdirSync, existsSync } from 'node:fs';
import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test, type Page } from '@playwright/test';

// CRO-34: the session panel with the gate test 2 replay, in headless Edge. No bridge, no sim: the built UI
// (ui/dist) is served from here and /api/status answers with one frame of the replay (the bridge's panel
// builder wrote them: ui/src/test/gate-test-2-frames.json). Saves the screenshots to docs/design/ui/screens/,
// in the light and the dark Windows theme (CRO-39), and checks that only the lists scroll, never the window.
//   npm run e2e:panel          (Linux: CROSSCHECK_BROWSER=chromium npm run e2e:panel)

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const DIST = join(ROOT, 'ui', 'dist');
const SHOTS = join(ROOT, 'docs', 'design', 'ui', 'screens');

interface Frame { t_ms: number; at: string; name: string | null; panel: { red: { control: string; label: string; priority: number }[] }; groups: unknown[] }
const frames = (JSON.parse(readFileSync(join(ROOT, 'ui', 'src', 'test', 'gate-test-2-frames.json'), 'utf8')) as { frames: Frame[] }).frames;
const named = (name: string) => frames.find((f) => f.name === name)!;

/** The status the bridge would serve the guest (first officer, pilot monitoring) at that moment. */
function status(f: Frame) {
  return {
    phase: 'session', bridge_version: '0.5.0', last_error: null, update: null, log: null,
    this_computer: {
      sim: { connected: true, name: 'MSFS 2024' },
      aircraft: { name: 'Fenix A320', app_version: '3.0.0.7613', package_version: '3.0.0.7613', reading: false },
      profile: { id: 'fenix-a320', builds: ['3.0.0.7613'], hash: 'sha256:0' },
      server: 'ws://127.0.0.1:8787/ws',
    },
    start_blocked: null, preflight: null, join_code: 'K7RX2M', seat: 'guest', seat_side: 'fo', role: 'pm', is_pf: false,
    server: 'connected', rtt_ms: 41, peer: 'joined',
    crew: [{ side: 'fo', you: true, role: 'pm', state: 'connected' }, { side: 'captain', you: false, role: 'pf', state: 'connected' }],
    session_ttl_ms: 300000, last_compared_at: f.at, groups: f.groups,
    owned: [{ var: 'aircraft.fuel.left.amount.kg', label: 'Fuel', unit: 'kg', delta: -12, warn: false }],
    panel: f.panel, control_notice: null,
  };
}

/** The start screen: not in a session. */
function idleStatus() {
  const s = status(frames[0]!);
  return {
    ...s, phase: 'idle', join_code: null, seat: null, seat_side: null, role: null, is_pf: undefined, server: 'disconnected', rtt_ms: null,
    peer: null, crew: undefined, groups: undefined, owned: undefined, panel: undefined,
    log: { folder: 'C:\\Users\\pilot\\AppData\\Local\\Crosscheck\\logs', file: 'bridge-1.jsonl' },
  };
}

let server: Server;
let base = '';
let current: Frame = frames[0]!;
let idle = false;

const TYPES: Record<string, string> = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon' };

test.beforeAll(async () => {
  if (!existsSync(join(DIST, 'index.html'))) throw new Error('ui/dist is missing: run npm run build first (npm run e2e:panel does).');
  mkdirSync(SHOTS, { recursive: true });
  server = createServer((req, res) => {
    const path = new URL(req.url ?? '/', 'http://x').pathname;
    if (path === '/api/status') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(idle ? idleStatus() : status(current)));
      return;
    }
    if (path.startsWith('/api/')) { res.writeHead(404); res.end(); return; }
    const file = join(DIST, path === '/' ? 'index.html' : path);
    if (!file.startsWith(DIST) || !existsSync(file)) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { 'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream' });
    res.end(readFileSync(file));
  });
  await new Promise<void>((ok) => server.listen(0, '127.0.0.1', ok));
  base = `http://127.0.0.1:${(server.address() as AddressInfo).port}/`;
});

test.afterAll(() => new Promise<void>((ok) => server.close(() => ok())));

// The replay's times are UTC (the diagnosis uses the host's UTC clock).
test.use({ viewport: { width: 1280, height: 800 }, timezoneId: 'UTC' });

async function show(page: Page, f: Frame) {
  idle = false;
  current = f;
  // The page's clock is the replay's: "Last compared" and the times read as they did then.
  await page.clock.setFixedTime(new Date(f.at).getTime() + 1000);
  await page.goto(base);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(f.panel.red[0]?.label ?? 'All controls match');
  await page.evaluate(() => document.fonts.ready);
}

async function shot(page: Page, name: string) {
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: join(SHOTS, `${name}.png`) });
}

/** A desktop window: the page itself never scrolls; only lists inside their pane do. */
async function fitsTheWindow(page: Page) {
  const { scroll, height, scrollW, width } = await page.evaluate(() => ({
    scroll: document.documentElement.scrollHeight, height: window.innerHeight, scrollW: document.documentElement.scrollWidth, width: window.innerWidth,
  }));
  expect(scroll, 'page height').toBeLessThanOrEqual(height);
  expect(scrollW, 'page width').toBeLessThanOrEqual(width);
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme });

    test(`session panel with the gate test 2 replay: screenshots for the design review (${scheme})`, async ({ page }) => {
      const foreign: string[] = [];
      page.on('request', (r) => {
        const u = new URL(r.url());
        if (u.hostname !== '127.0.0.1' && u.protocol !== 'data:') foreign.push(r.url());
      });

      // 20:07:48, the busiest moment: 7 red lines (the old panel had 19 cards). Collapsed by default.
      const peak = named('peak');
      await show(page, peak);
      await expect(page.getByText(`+${peak.panel.red.length - 1} more`)).toBeVisible();
      await expect(page.getByRole('list', { name: 'Log' })).toBeVisible();
      await expect(page.getByRole('contentinfo').getByRole('button', { name: 'Leave' })).toBeVisible();
      await fitsTheWindow(page);
      await shot(page, `session-peak-${scheme}`);

      // Show: the full red list, one line per control, scrolling inside the left pane.
      await page.getByRole('button', { name: 'Show' }).first().click();
      for (const l of peak.panel.red.slice(1)) await expect(page.getByRole('heading', { level: 2, name: l.label, exact: true })).toBeAttached();
      await fitsTheWindow(page);
      await shot(page, `session-expanded-${scheme}`);

      // Every collapsed line opened: not synced yet, outside the aircraft, indicators. Still no page scroll.
      const collapsed = named('collapsed');
      await show(page, collapsed);
      const more = page.locator('.notes').getByRole('button', { name: 'Show' });
      while (await more.count() > 0) await more.first().click();
      await expect(page.getByRole('list', { name: 'Not synced yet' })).toBeAttached();
      await expect(page.getByRole('list', { name: 'Outside the aircraft' })).toBeAttached();
      await expect(page.getByRole('list', { name: 'Indicators that differ' })).toBeAttached();
      await fitsTheWindow(page);

      await show(page, named('calm'));
      await expect(page.getByRole('heading', { level: 1 })).toHaveText('All controls match');
      await shot(page, `session-calm-${scheme}`);

      // The smallest window (the bridge's minimum client size): still fits.
      await page.setViewportSize({ width: 900, height: 600 });
      await show(page, peak);
      await page.getByRole('button', { name: 'Show' }).first().click();
      await fitsTheWindow(page);
      await page.setViewportSize({ width: 1280, height: 800 });

      // The start screen.
      idle = true;
      await page.goto(base);
      await expect(page.getByRole('heading', { level: 1 })).toHaveText('Start a shared cockpit');
      await fitsTheWindow(page);
      await shot(page, `start-${scheme}`);
      await page.setViewportSize({ width: 900, height: 600 });
      await fitsTheWindow(page);
      await page.setViewportSize({ width: 1280, height: 800 });
      idle = false;

      expect(foreign).toEqual([]);
    });
  });
}

test('the headline does not jump while the page polls', async ({ page }) => {
  // From 20:06:03 to the peak a line of the same priority appears, and the headline stays.
  const peak = named('peak');
  const before = frames[frames.indexOf(peak) - 1]!;
  await show(page, before);
  const headline = await page.getByRole('heading', { level: 1 }).textContent();
  current = peak;
  await expect(page.getByText(`+${peak.panel.red.length - 1} more`)).toBeVisible({ timeout: 5000 });
  const kept = peak.panel.red.find((l) => l.label === headline);
  if (kept && kept.priority <= peak.panel.red[0]!.priority) await expect(page.getByRole('heading', { level: 1 })).toHaveText(headline!);
});
