import { afterEach, describe, expect, it } from 'vitest';
import WebSocket from 'ws';
import { OUTDATED_MESSAGE, PROTOCOL_VERSION } from '../src/protocol/types.js';
import { ProtocolValidator } from '../src/protocol/validate.js';
import { FakeBridge, PREFLIGHT } from './fake/fakeBridge.js';
import { FakeSim } from './fake/fakeSim.js';
import { body, sleep, startServer, type TestServer } from './fake/harness.js';

let server: TestServer;
const sockets: WebSocket[] = [];
const bridges: FakeBridge[] = [];
afterEach(async () => {
  for (const ws of sockets.splice(0)) ws.terminate();
  for (const b of bridges.splice(0)) b.close();
  await server?.close();
});

/** What older zips send: 0.1.0 = protocol v1, 0.2.0 = protocol v2, 0.3.x = protocol v3, 0.4.x = protocol v4 envelopes. */
const OLD_CLIENTS: [number, string][] = [[1, '0.1.0'], [2, '0.2.0'], [3, '0.3.2'], [4, '0.4.0'], [5, '0.5.0']];

interface Reply { v: number; type: string; session: string | null; body: { code?: string; message?: string } }

async function oldClient(): Promise<{ ws: WebSocket; replies: Reply[] }> {
  const ws = new WebSocket(server.url);
  sockets.push(ws);
  await new Promise((r) => ws.once('open', r));
  const replies: Reply[] = [];
  ws.on('message', (d) => replies.push(JSON.parse(d.toString()) as Reply));
  return { ws, replies };
}

async function nextReply(replies: Reply[], count: number): Promise<Reply> {
  const start = Date.now();
  while (replies.length < count) {
    if (Date.now() - start > 2000) throw new Error('no reply');
    await sleep(5);
  }
  return replies[count - 1]!;
}

async function hostSession(): Promise<FakeBridge> {
  const host = new FakeBridge({ url: server.url, name: 'host', sim: new FakeSim() });
  bridges.push(host);
  await host.connect();
  await host.create();
  return host;
}

describe('22. version gating', () => {
  it('/health shows protocol 6', async () => {
    server = await startServer();
    const res = await server.app.inject({ method: 'GET', url: '/health' });
    expect(PROTOCOL_VERSION).toBe(6);
    expect(res.json()).toMatchObject({ protocol: 6 });
  });

  it.each(OLD_CLIENTS)('a protocol v%i (bridge %s) client is refused at preflight and at join, in an envelope it can read, with a clear message', async (v, version) => {
    const OLD_PREFLIGHT = { ...PREFLIGHT, bridge_version: version };
    server = await startServer();
    const host = await hostSession();
    const { ws, replies } = await oldClient();

    ws.send(JSON.stringify({ v, type: 'preflight_check', session: null, ts: 1, body: { join_code: host.joinCode, preflight: OLD_PREFLIGHT } }));
    const check = await nextReply(replies, 1);
    ws.send(JSON.stringify({ v, type: 'join', session: null, ts: 2, body: { join_code: host.joinCode, preflight: OLD_PREFLIGHT } }));
    const join = await nextReply(replies, 2);
    ws.send(JSON.stringify({ v, type: 'create', session: null, ts: 3, body: { preflight: OLD_PREFLIGHT } }));
    const create = await nextReply(replies, 3);

    for (const reply of [check, join, create]) {
      expect(reply).toMatchObject({ v, type: 'error', session: null, body: { code: 'unsupported_version', message: OUTDATED_MESSAGE } });
      expect(reply.body.message).toBe('Your Crosscheck is out of date. Download the latest version.');
      // The body is what every protocol version's error schema accepts.
      expect(new ProtocolValidator().validate({ ...reply, v: PROTOCOL_VERSION }).ok).toBe(true);
    }
    expect(ws.readyState).toBe(WebSocket.OPEN);

    // The old client took no slot: the host saw nobody, and a 0.6.0 pilot still joins.
    await sleep(50);
    expect(host.messages('peer_status')).toHaveLength(0);
    const guest = new FakeBridge({ url: server.url, name: 'guest', sim: new FakeSim() });
    bridges.push(guest);
    await guest.connect();
    expect(PREFLIGHT.bridge_version).toBe('0.6.0');
    expect((await guest.join(host.joinCode)).type).toBe('joined');
  });

  it('a 0.6.0 client is allowed: preflight_check answers with the comparison, join succeeds', async () => {
    server = await startServer();
    const host = await hostSession();
    const guest = new FakeBridge({ url: server.url, name: 'guest', sim: new FakeSim() });
    bridges.push(guest);
    await guest.connect();
    guest.send('preflight_check', { join_code: host.joinCode, preflight: PREFLIGHT }, null);
    expect(body(await guest.waitFor('preflight_result'))).toMatchObject({ rejects: [], warnings: [] });
    expect((await guest.join(host.joinCode)).type).toBe('joined');
  });

  it('a newer client is told that the server is the old one', async () => {
    server = await startServer();
    const { ws, replies } = await oldClient();
    ws.send(JSON.stringify({ v: 7, type: 'create', session: null, ts: 1, body: { preflight: PREFLIGHT } }));
    const reply = await nextReply(replies, 1);
    expect(reply).toMatchObject({ v: 7, type: 'error', body: { code: 'unsupported_version' } });
    expect(reply.body.message).toMatch(/server runs an older Crosscheck version/);
  });
});
