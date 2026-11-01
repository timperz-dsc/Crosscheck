import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

const VAR = 'S_OH_ELEC_BAT2';

/** The PM's aircraft keeps switching BAT2 back to 1 after every write; the PF has 0. */
async function loopUntilHeld(p: Pair): Promise<void> {
  p.guest.sim.logic(VAR, 1);
  p.guest.sim.stick(VAR, 1);
  await p.host.waitFor('desync', (m) => body(m).reason === 'correction_loop', { timeoutMs: 8000 });
  await p.guest.waitFor('desync', (m) => body(m).reason === 'correction_loop');
}

describe('11. correction loop guard', () => {
  it('stops after 3 corrections within 60 s, raises one "correction_loop" desync, clears when values match on their own', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    await loopUntilHeld(pair);

    const max = server.config.correctionLoopMax;
    expect(guest.sim.writesBy('correction').filter((w) => w.name === VAR)).toHaveLength(max);
    const desync = host.messages('desync', (m) => body(m).reason === 'correction_loop');
    expect(desync).toHaveLength(1);
    expect(body(desync[0]!)).toMatchObject({ var: VAR, group: 'overhead', values: { pf: 0, pm: 1 } });

    // Held: more mismatching rounds, but no 4th write and no repeated alarm.
    await sleep(server.config.snapshotIntervalMs * 6);
    expect(guest.sim.writesBy('correction').filter((w) => w.name === VAR)).toHaveLength(max);
    expect(host.messages('desync', (m) => body(m).reason === 'correction_loop')).toHaveLength(1);
    expect(host.sim.writesBy('correction')).toHaveLength(0);

    // The values come together without anyone writing (the PF's aircraft switches it too).
    host.sim.logic(VAR, 1);
    const cleared = await host.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === VAR);
    expect(body(cleared).reason).toBe('correction_loop');
    await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === VAR);
  });

  it('is cleared when a pilot acts on the var', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    await loopUntilHeld(pair);
    guest.sim.unstick(VAR);

    const from = guest.mark();
    const id = host.set(VAR, 0);
    const cleared = await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === VAR, { from });
    expect(body(cleared).reason).toBe('correction_loop');
    await host.waitFor('ack', (m) => body(m).id === id);
    expect(guest.sim.get(VAR)).toBe(0);
    expect(host.sim.hash('overhead')).toBe(guest.sim.hash('overhead'));
  });
});
