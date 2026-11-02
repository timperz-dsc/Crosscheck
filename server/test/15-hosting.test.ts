import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SERVER_VERSION } from '../src/app.js';
import { SessionLog } from '../src/log/jsonl.js';
import { RateLimiter } from '../src/session/rateLimit.js';
import { FakeBridge, PREFLIGHT } from './fake/fakeBridge.js';
import { FakeSim } from './fake/fakeSim.js';
import { body, startServer, type TestServer } from './fake/harness.js';

let server: TestServer;
const bridges: FakeBridge[] = [];
afterEach(async () => {
  vi.restoreAllMocks();
  await server?.close();
  for (const b of bridges.splice(0)) b.close();
});

async function bridge(): Promise<FakeBridge> {
  const b = new FakeBridge({ url: server.url, name: 'b', sim: new FakeSim() });
  bridges.push(b);
  await b.connect();
  return b;
}

describe('15. hosting', () => {
  it('/health answers 200 with the server version', async () => {
    server = await startServer();
    const res = await server.app.inject({ method: 'GET', url: '/health' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toMatchObject({ ok: true, version: SERVER_VERSION, protocol: 4 });
    expect(SERVER_VERSION).toMatch(/^\d+\.\d+\.\d+/);
  });

  it('limits create per IP and says when to try again', async () => {
    server = await startServer({ createLimit: 2 });
    for (let i = 0; i < 2; i++) await (await bridge()).create();
    const third = await bridge();
    third.send('create', { preflight: PREFLIGHT, name: 'x' }, null);
    const err = await third.waitFor('error');
    expect(body<{ code: string; message: string }>(err).code).toBe('forbidden');
    expect(body<{ message: string }>(err).message).toMatch(/Too many create attempts.*Try again in about \d+ min/);
  });

  it('limits join and preflight_check together', async () => {
    server = await startServer({ joinLimit: 2 });
    const host = await bridge();
    await host.create();
    const guest = await bridge();
    guest.send('join', { join_code: 'ZZZZZZ', preflight: PREFLIGHT }, null);
    await guest.waitFor('join_rejected');
    guest.send('preflight_check', { join_code: 'ZZZZZZ', preflight: PREFLIGHT }, null);
    await guest.waitFor('join_rejected', () => true, { from: guest.received.length });
    guest.send('join', { join_code: host.joinCode, preflight: PREFLIGHT }, null);
    const err = await guest.waitFor('error');
    expect(body<{ code: string }>(err).code).toBe('forbidden');
  });

  it('rate limiter window slides', () => {
    const rl = new RateLimiter(2, 1000);
    expect(rl.hit('a', 0)).toBe(0);
    expect(rl.hit('a', 100)).toBe(0);
    expect(rl.hit('a', 200)).toBe(800);
    expect(rl.hit('b', 200)).toBe(0);
    expect(rl.hit('a', 1000)).toBe(0);
    rl.prune(5000);
    expect(rl.hit('a', 5000)).toBe(0);
  });

  it('prints one line per session event to stdout, without the join code', async () => {
    const lines: string[] = [];
    vi.spyOn(console, 'log').mockImplementation((l: unknown) => { lines.push(String(l)); });
    server = await startServer({ echoEvents: true });
    const host = await bridge();
    await host.create();
    const created = lines.map((l) => JSON.parse(l) as Record<string, unknown>).find((l) => l.event === 'session_created');
    expect(created).toBeDefined();
    expect(created?.session).toBeTypeOf('string');
    expect(lines.join('\n')).not.toContain(host.joinCode);
  });

  it('an unwritable log folder does not stop the session', async () => {
    const err = vi.spyOn(console, 'error').mockImplementation(() => {});
    const file = join(mkdtempSync(join(tmpdir(), 'crosscheck-test-')), 'not-a-folder');
    writeFileSync(file, 'x');
    const log = new SessionLog(join(file, 'logs'), 'abc');
    expect(log.path).toBeNull();
    expect(() => log.event('x')).not.toThrow();
    expect(err).toHaveBeenCalled();
  });
});
