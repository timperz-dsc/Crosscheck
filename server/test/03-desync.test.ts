import { afterEach, describe, expect, it } from 'vitest';
import { body, startPair, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

describe('3. never acked → DesyncEvent after 3 retries', () => {
  it('sends the command 4 times (first + 3 re-sends), then a desync to both pilots', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    guest.dropAcks = true;

    const id = host.set('S_OH_ELEC_BAT1', 1);
    const hostDesync = await host.waitFor('desync', (m) => body(m).command_id === id);
    const guestDesync = await guest.waitFor('desync', (m) => body(m).command_id === id);

    expect(guest.messages('command', (m) => body(m).id === id).map((m) => body(m).attempt)).toEqual([0, 1, 2, 3]);
    for (const d of [hostDesync, guestDesync]) {
      expect(body(d)).toMatchObject({ var: 'S_OH_ELEC_BAT1', reason: 'no_ack', expected: 1, observed: null, client_id: guest.clientId });
    }
    expect(hostDesync.seq).toBe(guestDesync.seq);
    expect(server.logLines().some((l) => (l.msg as { type?: string } | undefined)?.type === 'desync')).toBe(true);
  });

  it('a value that refuses to change → re-sent, then desync "refused" with the observed value', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.refuse('S_FCU_AP1');

    const id = host.set('S_FCU_AP1', 1);
    const desync = await host.waitFor('desync', (m) => body(m).command_id === id);
    expect(body(desync)).toMatchObject({ var: 'S_FCU_AP1', reason: 'refused', expected: 1, observed: 0 });
    expect(guest.messages('command', (m) => body(m).id === id)).toHaveLength(4);
    // The origin also gets the failed ack.
    expect(host.messages('ack', (m) => body(m).id === id).map((m) => body(m).ok)).toEqual([false]);
    await guest.waitFor('desync', (m) => body(m).command_id === id);
  });
});
