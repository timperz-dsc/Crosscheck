import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, type Pair } from './fake/harness.js';
import { FakeSim, TEST_VARS, type VarDef } from './fake/fakeSim.js';

let pair: Pair;
afterEach(async () => pair?.close());

const FUEL = 'aircraft.fuel.left.amount.kg';
const withFuel = (kg: number): VarDef[] => [...TEST_VARS, { name: FUEL, cls: 'owned', group: 'systems', init: kg, step: 50 }];
interface Drift { group: string; values: { var: string; pf: number; pm: number; delta: number }[] }

describe('19. owned drift is rate limited (step, at most every 10 s)', () => {
  it('a steady difference is sent once, not after every comparison', async () => {
    pair = await startPair({ host: { sim: new FakeSim(withFuel(3750)) }, guest: { sim: new FakeSim(withFuel(1500)) } });
    const { guest, server } = pair;
    await guest.waitFor('drift', (m) => body<Drift>(m).values.length === 1);
    // The hash keeps differing, so snapshots are requested again and again.
    await sleep(server.config.driftMinIntervalMs * 1.5);
    expect(guest.messages('drift')).toHaveLength(1);
    expect(guest.messages('desync')).toHaveLength(0);
  });

  it('a change larger than the step is sent at once; small changes wait for the interval', async () => {
    pair = await startPair({ host: { sim: new FakeSim(withFuel(1500)) }, guest: { sim: new FakeSim(withFuel(1100)) } });
    const { guest, server } = pair;
    await guest.waitFor('drift', (m) => body<Drift>(m).values.length === 1);
    const t0 = Date.now();

    guest.sim.logic(FUEL, 1000); // 100 kg more: above the 50 kg step
    const big = await guest.waitFor('drift', (m) => body<Drift>(m).values[0]?.pm === 1000);
    expect(Date.now() - t0).toBeLessThan(server.config.driftMinIntervalMs);
    expect(body<Drift>(big).values[0]).toMatchObject({ delta: -500 });

    const t1 = Date.now();
    guest.sim.logic(FUEL, 990); // 10 kg: below the step
    await guest.waitFor('drift', (m) => body<Drift>(m).values[0]?.pm === 990, { timeoutMs: server.config.driftMinIntervalMs * 4 });
    expect(Date.now() - t1).toBeGreaterThanOrEqual(server.config.driftMinIntervalMs * 0.8);
  });

  it('when the difference is gone, the empty drift is sent at once', async () => {
    pair = await startPair({ host: { sim: new FakeSim(withFuel(1500)) }, guest: { sim: new FakeSim(withFuel(1100)) } });
    const { guest } = pair;
    await guest.waitFor('drift', (m) => body<Drift>(m).values.length === 1);
    guest.sim.logic(FUEL, 1500);
    await guest.waitFor('drift', (m) => body<Drift>(m).values.length === 0);
  });
});
