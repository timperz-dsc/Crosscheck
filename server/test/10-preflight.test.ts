import { afterEach, describe, expect, it } from 'vitest';
import type { Preflight } from '../src/protocol/types.js';
import { FakeBridge, PREFLIGHT } from './fake/fakeBridge.js';
import { FakeSim } from './fake/fakeSim.js';
import { body, sleep, startServer, type TestServer } from './fake/harness.js';

let server: TestServer;
const bridges: FakeBridge[] = [];
afterEach(async () => {
  await server?.close();
  for (const b of bridges.splice(0)) b.close();
});

async function bridge(name: string, preflight: Preflight = PREFLIGHT): Promise<FakeBridge> {
  const b = new FakeBridge({ url: server.url, name, sim: new FakeSim(), preflight });
  bridges.push(b);
  await b.connect();
  return b;
}

async function hostSession(): Promise<FakeBridge> {
  const host = await bridge('host');
  await host.create();
  return host;
}

describe('10. preflight on join', () => {
  it('rejects a profile hash mismatch with a clear diff', async () => {
    server = await startServer();
    const host = await hostSession();
    const guest = await bridge('guest', { ...PREFLIGHT, profile_hash: 'sha256:0d1e2f3a4b5c6d7e' });

    const answer = await guest.join(host.joinCode);
    expect(answer.type).toBe('join_rejected');
    expect(body(answer)).toMatchObject({
      reason: 'preflight_mismatch',
      diff: [{ field: 'profile_hash', reference: PREFLIGHT.profile_hash, joining: 'sha256:0d1e2f3a4b5c6d7e', severity: 'reject' }],
    });
    await sleep(50);
    expect(host.messages('peer_status')).toHaveLength(0);
    expect(server.logLines().some((l) => l.event === 'join_rejected')).toBe(true);
  });

  it('warns but does not reject on an AIRAC mismatch; both pilots see the warning', async () => {
    server = await startServer();
    const host = await hostSession();
    const guest = await bridge('guest', { ...PREFLIGHT, airac: '2610' });

    const answer = await guest.join(host.joinCode);
    expect(answer.type).toBe('joined');
    const warning = { field: 'airac', reference: '2611', joining: '2610', severity: 'warn' };
    expect(body(answer).warnings).toEqual([expect.objectContaining(warning)]);
    const status = await host.waitFor('peer_status', (m) => body(m).state === 'joined');
    expect(body(status).warnings).toEqual([expect.objectContaining(warning)]);
  });

  it('rejects a different bridge major version and a different aircraft build, all fields listed', async () => {
    server = await startServer();
    const host = await hostSession();
    const guest = await bridge('guest', {
      ...PREFLIGHT, bridge_version: '1.0.0', aircraft: { package_version: '3.0.1.7700', app_version: '3.0.1.340' }, airac: null,
    });
    const answer = await guest.join(host.joinCode);
    expect(answer.type).toBe('join_rejected');
    const diff = body<{ diff: { field: string; severity: string }[] }>(answer).diff;
    expect(diff.map((d) => `${d.field}:${d.severity}`)).toEqual([
      'bridge_version:reject', 'aircraft.package_version:reject', 'aircraft.app_version:reject', 'airac:warn',
    ]);
  });

  it('accepts a different bridge minor version; join codes are case-insensitive', async () => {
    server = await startServer();
    const host = await hostSession();
    const guest = await bridge('guest', { ...PREFLIGHT, bridge_version: '0.4.2' });
    const answer = await guest.join(host.joinCode.toLowerCase());
    expect(answer.type).toBe('joined');
    expect(body(answer).warnings).toEqual([]);
  });

  it('rejects unknown codes and a third pilot', async () => {
    server = await startServer();
    const host = await hostSession();
    const stranger = await bridge('stranger');
    expect(body(await stranger.join('ZZZZZZ'))).toMatchObject({ reason: 'unknown_code' });

    const guest = await bridge('guest');
    expect((await guest.join(host.joinCode)).type).toBe('joined');
    const third = await bridge('third');
    expect(body(await third.join(host.joinCode))).toMatchObject({ reason: 'session_full' });
  });
});
