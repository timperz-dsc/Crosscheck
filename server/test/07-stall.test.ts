import { afterEach, describe, expect, it } from 'vitest';
import { body, startPair, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

describe('7. one-way stall (socket open, no messages from one side)', () => {
  it('client stops sending (socket stays open) → lost after 5 s, the other pilot is told', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    const lostAfter = server.config.lostAfterMs;

    const start = Date.now();
    guest.mute = true;
    const status = await host.waitFor('peer_status', (m) => body(m).state === 'lost');
    const elapsed = Date.now() - start;

    expect(body(status)).toMatchObject({ client_id: guest.clientId, reason: 'no_messages' });
    // Counted from the last message received, which is at most one heartbeat before the stall began.
    expect(elapsed).toBeGreaterThanOrEqual(lostAfter - server.config.heartbeatMs);
    expect(elapsed).toBeLessThan(lostAfter * 4);
    expect(server.logLines().some((l) => l.event === 'client_lost' && l.reason === 'no_messages')).toBe(true);
  });

  it('client still sends but receives nothing (pongs missing) → lost as well', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;

    const start = Date.now();
    guest.deaf = true; // keeps sending its own pings, never answers ours
    const status = await host.waitFor('peer_status', (m) => body(m).state === 'lost');

    expect(body(status)).toMatchObject({ client_id: guest.clientId, reason: 'no_pong' });
    expect(Date.now() - start).toBeGreaterThanOrEqual(server.config.lostAfterMs - server.config.heartbeatMs);
  });

  it('after a stall the client can resume', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.mute = true;
    await host.waitFor('peer_status', (m) => body(m).state === 'lost');
    const id = host.set('S_OH_ELEC_BAT1', 1);
    const resumed = await guest.resume();
    expect(resumed.type).toBe('resumed');
    await host.waitFor('ack', (m) => body(m).id === id);
    expect(guest.sim.get('S_OH_ELEC_BAT1')).toBe(1);
  });
});
