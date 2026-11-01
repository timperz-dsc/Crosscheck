import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';
import { FakeSim, TEST_VARS, type VarDef } from './fake/fakeSim.js';

let pair: Pair;
afterEach(async () => pair?.close());

const FUEL = 'aircraft.fuel.left.amount.kg';
const withFuel = (kg: number): VarDef[] => [...TEST_VARS, { name: FUEL, cls: 'owned', group: 'systems', init: kg }];

interface Drift { group: string; values: { var: string; pf: number; pm: number; delta: number }[] }

describe('14. owned values (fuel): never written, only drift', () => {
  it('a fuel difference at join is reported as drift, not aligned', async () => {
    pair = await startPair({ host: { sim: new FakeSim(withFuel(1500)) }, guest: { sim: new FakeSim(withFuel(1100)) } });
    const { host, guest } = pair;
    const drift = await guest.waitFor('drift', (m) => body<Drift>(m).values.length === 1);
    expect(body<Drift>(drift)).toEqual({ group: 'systems', values: [{ var: FUEL, pf: 1500, pm: 1100, delta: -400 }] });
    await host.waitFor('drift', (m) => body<Drift>(m).values.length === 1);
    await sleep(pair.server.config.snapshotIntervalMs * 3);
    expect(guest.sim.get(FUEL)).toBe(1100);
    expect([...host.messages('correction'), ...guest.messages('correction')]).toHaveLength(0);
    expect([...host.messages('desync'), ...guest.messages('desync')]).toHaveLength(0);
  });

  it('changes during the session are reported as drift, never corrected or raised', async () => {
    pair = await startPair({ host: { sim: new FakeSim(withFuel(1500)) }, guest: { sim: new FakeSim(withFuel(1500)) } });
    const { host, guest, server } = pair;

    // During the session Fenix keeps changing fuel on one side.
    const mark = [host.mark(), guest.mark()];
    for (const kg of [1495, 1490, 1488]) {
      guest.sim.logic(FUEL, kg);
      await sleep(server.config.snapshotIntervalMs * 2);
    }
    const drift = await host.waitFor('drift', (m) => body<Drift>(m).values.some((v) => v.pm === 1488), { from: mark[0] });
    expect(body<Drift>(drift)).toEqual({ group: 'systems', values: [{ var: FUEL, pf: 1500, pm: 1488, delta: -12 }] });
    expect(drift.seq).toBeUndefined();
    await guest.waitFor('drift', (m) => body<Drift>(m).values.length === 1, { from: mark[1] });

    expect(guest.messages('correction', (m) => body(m).var === FUEL)).toHaveLength(0);
    expect([...host.messages('desync'), ...guest.messages('desync')]).toHaveLength(0);
    expect(host.messages('snapshot_diff', (m) => (body<{ differences: { var: string }[] }>(m).differences).some((d) => d.var === FUEL), mark[0])).toHaveLength(0);
    expect(server.logLines().some((l) => l.event === 'desync_still_open' || l.event === 'correction_loop')).toBe(false);

    // Equal again → drift with no values clears it.
    const again = host.mark();
    guest.sim.logic(FUEL, 1500);
    await host.waitFor('drift', (m) => body<Drift>(m).values.length === 0, { from: again });
  });

  it('a real control in the same group is still corrected while fuel drifts', async () => {
    pair = await startPair({ host: { sim: new FakeSim(withFuel(1500)) }, guest: { sim: new FakeSim(withFuel(1500)) } });
    const { host, guest } = pair;
    guest.sim.logic(FUEL, 1490);
    guest.sim.logic('S_OH_ELEC_BAT2', 1); // overhead differs too, PM is corrected to the PF
    await waitUntil(() => guest.sim.get('S_OH_ELEC_BAT2') === 0, 'switch corrected');
    await host.waitFor('drift', (m) => body<Drift>(m).values.length === 1);
    expect(guest.messages('correction', (m) => body(m).var === FUEL)).toHaveLength(0);
  });
});
