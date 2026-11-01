import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import type { AddressInfo } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { buildServer, type CrosscheckServer } from '../../src/app.js';
import { DEFAULT_CONFIG, scaleTimings, type ServerConfig } from '../../src/config.js';
import { FakeBridge, type BridgeOptions } from './fakeBridge.js';
import { FakeSim } from './fakeSim.js';

/**
 * Tests run the real server logic with every timing scaled down 20×:
 * heartbeat 1 s → 50 ms, lost after 5 s → 250 ms, session kept 5 min → 15 s, snapshots 2 s → 100 ms.
 */
export const TIME_SCALE = 0.05;
export const scaled = (ms: number) => Math.max(1, Math.round(ms * TIME_SCALE));

export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export async function waitUntil(cond: () => boolean, label: string, timeoutMs = 5000): Promise<void> {
  const start = Date.now();
  while (!cond()) {
    if (Date.now() - start > timeoutMs) throw new Error(`timed out: ${label}`);
    await sleep(5);
  }
}

export interface TestServer extends CrosscheckServer {
  url: string;
  logDir: string;
  /** All JSONL lines of all session logs written so far. */
  logLines(): Record<string, unknown>[];
  close(): Promise<void>;
}

export async function startServer(overrides: Partial<ServerConfig> = {}): Promise<TestServer> {
  const logDir = mkdtempSync(join(tmpdir(), 'crosscheck-test-'));
  const config: ServerConfig = { ...scaleTimings(DEFAULT_CONFIG, TIME_SCALE), logDir, validateOutgoing: true, ...overrides };
  const srv = await buildServer(config);
  await srv.app.listen({ port: 0, host: '127.0.0.1' });
  const port = (srv.app.server.address() as AddressInfo).port;
  return {
    ...srv,
    url: `ws://127.0.0.1:${port}/ws`,
    logDir,
    logLines: () =>
      readdirSync(logDir)
        .filter((f) => f.endsWith('.jsonl'))
        .flatMap((f) => readFileSync(join(logDir, f), 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l) as Record<string, unknown>)),
    close: async () => {
      await srv.app.close();
      rmSync(logDir, { recursive: true, force: true });
    },
  };
}

export interface Pair {
  server: TestServer;
  host: FakeBridge;
  guest: FakeBridge;
  close(): Promise<void>;
}

/** A server plus host (PF) and guest (PM) with identical sims, after the join resync. */
export async function startPair(opts: {
  server?: Partial<ServerConfig>;
  host?: Partial<BridgeOptions>;
  guest?: Partial<BridgeOptions>;
} = {}): Promise<Pair> {
  const server = await startServer(opts.server);
  const host = new FakeBridge({ url: server.url, name: 'host', sim: new FakeSim(), ...opts.host });
  await host.connect();
  await host.create();
  const guest = new FakeBridge({ url: server.url, name: 'guest', sim: new FakeSim(), ...opts.guest });
  await guest.connect();
  const joined = await guest.join(host.joinCode);
  if (joined.type !== 'joined') throw new Error(`join failed: ${JSON.stringify(joined.body)}`);
  // Join triggers a full snapshot request; wait until both answered it.
  await host.waitFor('snapshot_request', (m) => (m.body as { reason: string }).reason === 'join');
  await guest.waitFor('snapshot_request', (m) => (m.body as { reason: string }).reason === 'join');
  await sleep(20);
  return {
    server, host, guest,
    close: async () => {
      await server.close();
      host.close();
      guest.close();
      if (host.invalid.length + guest.invalid.length > 0) {
        throw new Error(`server sent invalid messages: ${[...host.invalid, ...guest.invalid].join('; ')}`);
      }
    },
  };
}

export const body = <T = Record<string, unknown>>(m: { body: unknown }) => m.body as T;
