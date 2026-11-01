import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

const FD = 'S_FCU_EFIS1_FD';
const LAMP = 'I_FCU_EFIS1_FD';

describe('20. pulse effect is compared between the two sims (cause I)', () => {
  it('the same effect on both sides: no desync', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    const id = host.pressWithEffect(FD);
    await host.waitFor('ack', (m) => body(m).id === id);
    await sleep(server.config.ackMarginMs * 4);
    expect(guest.sim.get(LAMP)).toBe(0);
    expect(host.sim.get(LAMP)).toBe(0);
    expect(host.messages('desync')).toHaveLength(0);
  });

  it('no effect on either side (system unpowered) is not a failure', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    host.sim.unpower(FD);
    guest.sim.unpower(FD);
    const id = host.pressWithEffect(FD);
    await host.waitFor('ack', (m) => body(m).id === id);
    await sleep(server.config.ackMarginMs * 4);
    expect(host.messages('desync')).toHaveLength(0);
  });

  it('a different effect is a red desync with both values', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.unpower(FD); // the press arrives, but the guest FD does not change
    const id = host.pressWithEffect(FD);
    const d = await guest.waitFor('desync', (m) => body(m).reason === 'effect_mismatch');
    expect(body(d)).toMatchObject({ var: FD, tier: 'red', command_id: id, expected: 0, observed: 1, values: { pf: 0, pm: 1 } });
    await host.waitFor('desync', (m) => body(m).reason === 'effect_mismatch');
  });

  it('a missing report from the sender is red too (result unknown)', async () => {
    pair = await startPair();
    const { host } = pair;
    host.pressWithEffect(FD, { reportAfterMs: null });
    const d = await host.waitFor('desync', (m) => body(m).reason === 'effect_mismatch');
    expect(body(d)).toMatchObject({ var: FD, tier: 'red', expected: null });
    expect(body<{ message: string }>(d).message).toContain('unknown');
  });

  it('parity mode compares odd/even only', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    // A counter-like effect var: after the press the host shows 1 and the guest 3. Different values,
    // same parity (odd): equal for parity, a mismatch for same.
    host.sim.logic(LAMP, 0);
    guest.sim.logic(LAMP, 3);
    guest.sim.unpower(FD);
    const id = host.pressWithEffect(FD, { expect: 'parity' });
    await host.waitFor('ack', (m) => body(m).id === id);
    await sleep(server.config.ackMarginMs * 4);
    expect(host.messages('desync', (m) => body(m).reason === 'effect_mismatch')).toHaveLength(0);

    host.sim.logic(LAMP, 0);
    host.pressWithEffect(FD, { expect: 'same' });
    await host.waitFor('desync', (m) => body(m).reason === 'effect_mismatch');
  });

  it('a pulse is never re-sent: refused once → red at once, one delivery', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.refuse(FD);
    const id = host.press(FD);
    const d = await host.waitFor('desync', (m) => body(m).command_id === id);
    expect(body(d)).toMatchObject({ reason: 'refused', tier: 'red' });
    expect(body<{ message: string }>(d).message).not.toContain('re-sends');
    expect(guest.messages('command', (m) => body(m).id === id).map((m) => body(m).attempt)).toEqual([0]);
  });

  it('absolute values are still re-sent (3 times)', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.refuse('S_OH_ELEC_BAT1');
    const id = host.set('S_OH_ELEC_BAT1', 1);
    await host.waitFor('desync', (m) => body(m).command_id === id);
    expect(guest.messages('command', (m) => body(m).id === id).map((m) => body(m).attempt)).toEqual([0, 1, 2, 3]);
  });
});
