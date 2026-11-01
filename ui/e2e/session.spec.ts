import { spawn, type ChildProcess } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test, type Page } from '@playwright/test';

// Runs the session server and two bridges (`--sim fake`) and drives both pilots through the UI:
// create → code → join with preflight → both cockpits match → "I have control".
// Needs `npm install` in server/ and a built bridge (npm run e2e builds UI and bridge first).

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const procs: ChildProcess[] = [];
const logs = mkdtempSync(join(tmpdir(), 'crosscheck-e2e-'));
let hostUrl = '';
let guestUrl = '';

function freePort(): Promise<number> {
  return new Promise((ok) => {
    const s = createServer();
    s.listen(0, '127.0.0.1', () => {
      const port = (s.address() as { port: number }).port;
      s.close(() => ok(port));
    });
  });
}

function start(cmd: string, args: string[], cwd: string, ready: string, env: Record<string, string> = {}): Promise<void> {
  const p = spawn(cmd, args, { cwd, env: { ...process.env, ...env }, stdio: ['pipe', 'pipe', 'pipe'] });
  procs.push(p);
  let out = '';
  return new Promise((ok, fail) => {
    const timer = setTimeout(() => fail(new Error(`${cmd} did not start:\n${out}`)), 90_000);
    const look = (d: Buffer) => {
      out += d.toString();
      if (out.includes(ready)) {
        clearTimeout(timer);
        ok();
      }
    };
    p.stdout!.on('data', look);
    p.stderr!.on('data', look);
    p.on('exit', (code) => fail(new Error(`${cmd} exited with ${code}:\n${out}`)));
  });
}

async function bridge(name: string, apiPort: number, serverPort: number): Promise<string> {
  await start('dotnet', [
    'run', '--project', join(ROOT, 'bridge', 'Crosscheck.Bridge'), '--no-build', '--',
    '--sim', 'fake', '--no-browser', '--no-tray', '--name', name,
    '--api-port', String(apiPort), '--server', `ws://127.0.0.1:${serverPort}/ws`,
    '--ui-dir', join(ROOT, 'ui', 'dist'), '--log-dir', join(logs, name),
  ], ROOT, 'Crosscheck: http://127.0.0.1');
  return `http://127.0.0.1:${apiPort}/`;
}

test.beforeAll(async () => {
  const serverPort = await freePort();
  await start('node', [join('node_modules', 'tsx', 'dist', 'cli.mjs'), join('src', 'index.ts')], join(ROOT, 'server'), 'Crosscheck session server', {
    CROSSCHECK_PORT: String(serverPort), CROSSCHECK_HOST: '127.0.0.1', CROSSCHECK_LOG_DIR: join(logs, 'server'),
  });
  hostUrl = await bridge('host', await freePort(), serverPort);
  guestUrl = await bridge('guest', await freePort(), serverPort);
});

test.afterAll(() => {
  for (const p of procs) {
    if (p.pid && p.exitCode === null) spawn('taskkill', ['/pid', String(p.pid), '/T', '/F']);
  }
  try { rmSync(logs, { recursive: true, force: true }); } catch { /* still in use */ }
});

/** CROSSCHECK_SCREENSHOTS=<dir> saves each screen, to compare with docs/design/ui. */
async function shot(page: Page, name: string) {
  const dir = process.env.CROSSCHECK_SCREENSHOTS;
  if (!dir) return;
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: join(dir, `${name}.png`), fullPage: true });
}

/** Every request a page makes must stay on this computer. */
function watchNetwork(page: Page): string[] {
  const foreign: string[] = [];
  page.on('request', (r) => {
    const u = new URL(r.url());
    if (u.hostname !== '127.0.0.1' && u.protocol !== 'data:') foreign.push(r.url());
  });
  return foreign;
}

test('two pilots: create, preflight, join, both cockpits match, control change', async ({ browser }) => {
  const host = await (await browser.newContext()).newPage();
  const guest = await (await browser.newContext()).newPage();
  const hostForeign = watchNetwork(host);
  const guestForeign = watchNetwork(guest);

  // Captain creates.
  await host.goto(hostUrl);
  await expect(host.getByRole('heading', { level: 1 })).toHaveText('Start a shared cockpit');
  await expect(host.getByText('Connected to MSFS 2024')).toBeVisible();
  expect(await host.evaluate(async () => {
    await document.fonts.ready;
    return document.fonts.check('15px Geist') && document.fonts.check('15px "Geist Mono"');
  })).toBe(true);
  await host.setViewportSize({ width: 1280, height: 800 });
  await guest.setViewportSize({ width: 1280, height: 800 });
  await shot(host, '1-start');
  await host.getByRole('button', { name: 'Create session' }).click();
  await expect(host.getByRole('heading', { level: 1 })).toHaveText('Waiting for the first officer');
  const code = (await host.getByLabel('Session code').textContent())!.trim();
  expect(code).toMatch(/^[A-Z0-9]{6}$/);
  await shot(host, '1b-waiting');

  // First officer checks the setup, then joins.
  await guest.goto(guestUrl);
  await guest.getByLabel('Session code').fill(code);
  await guest.getByRole('button', { name: 'Join', exact: true }).click();
  await expect(guest.getByRole('heading', { level: 1 })).toHaveText("Your setup matches the captain's");
  await expect(guest.getByRole('row', { name: /Crosscheck bridge/ })).toContainText('Same');
  await expect(guest.getByRole('row', { name: /AIRAC/ })).toContainText('Unknown');
  await shot(guest, '2-preflight');
  await guest.getByRole('button', { name: 'Join session' }).click();

  // Both in the session.
  await expect(guest.getByRole('heading', { level: 1 })).toHaveText('Both cockpits match');
  await expect(host.getByRole('heading', { level: 1 })).toHaveText('Both cockpits match');
  await expect(host.getByText('Captain (you)')).toBeVisible();
  await expect(guest.getByText('First officer (you)')).toBeVisible();
  await expect(host.getByRole('button', { name: 'You have control' })).toBeVisible();
  await expect(guest.getByRole('button', { name: 'I have control' })).toBeVisible();
  await expect(guest.getByText('Main panel')).toBeVisible();
  await shot(guest, '3-session');

  // The first officer takes control.
  await guest.getByRole('button', { name: 'I have control' }).click();
  await expect(guest.getByRole('region', { name: 'You have control' })).toBeVisible();
  await expect(guest.getByRole('button', { name: 'You have control' })).toBeVisible();
  await expect(host.getByRole('button', { name: 'I have control' })).toBeVisible();
  await shot(guest, '5-control-change');

  // The captain leaves; back to the start screen.
  await host.getByRole('button', { name: 'Leave' }).click();
  await expect(host.getByRole('heading', { level: 1 })).toHaveText('Start a shared cockpit');

  expect([...hostForeign, ...guestForeign]).toEqual([]);
});
