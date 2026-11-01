import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

describe('5. two concurrent deltas on one encoder', () => {
  it('applies every delta on both sides; displays end on the same sum', async () => {
    pair = await startPair({ host: { latencyMs: 3 }, guest: { latencyMs: 3 } });
    const { host, guest } = pair;

    const ids: string[] = [];
    for (let i = 0; i < 5; i++) {
      ids.push(host.turn('E_FCU_HEADING', 1));
      if (i < 3) ids.push(guest.turn('E_FCU_HEADING', -1));
    }
    await waitUntil(() => ids.every((id) => [...host.received, ...guest.received].some((m) => m.type === 'ack' && body(m).id === id)), 'all deltas acked');

    expect(host.sim.display('E_FCU_HEADING')).toBe(102);
    expect(guest.sim.display('E_FCU_HEADING')).toBe(102);
    expect([...host.messages('notice'), ...guest.messages('notice')].filter((m) => body(m).kind === 'overridden')).toHaveLength(0);

    // The snapshots agree afterwards, so no correction is needed.
    await sleep(300);
    expect([...host.messages('correction'), ...guest.messages('correction')]).toHaveLength(0);
    expect(host.sim.hash('fcu')).toBe(guest.sim.hash('fcu'));
  });
});
