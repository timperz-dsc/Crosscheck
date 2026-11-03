import { afterEach, describe, expect, it } from 'vitest';
import { diffLoose } from '../src/session/corrections.js';
import { FakeSim, TEST_VARS, type VarDef } from './fake/fakeSim.js';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

// CRO-34: indicators with a tolerance (battery volts, brake pressure) and indicators of a unit with a
// power flag are compared value by value (snapshot_hash.loose), outside the exact group hash.
const EXTRA: VarDef[] = [
  { name: 'N_ELEC_VOLT_BAT_1', cls: 'observe', group: 'systems', init: 27.8, tolerance: 0.5 },
  { name: 'I_FCU_AP1_LAMP', cls: 'observe', group: 'fcu', init: 0, powered: true },
  {
    name: 'B_FCU_HEADING_DASHED', cls: 'mode', group: 'fcu', init: 0, powered: true, reportAs: 'E_FCU_HEADING',
    message: 'HDG managed differs between the cockpits – push or pull the knob in the cockpit that is wrong.',
  },
];
const sims = () => ({ host: { sim: new FakeSim([...TEST_VARS, ...EXTRA]) }, guest: { sim: new FakeSim([...TEST_VARS, ...EXTRA]) } });

describe('25. loose indicators: tolerance and power gate (CRO-34)', () => {
  it('diffLoose: within tolerance matches, over it differs, unpowered or missing is not compared', () => {
    const r = diffLoose(
      [
        { var: 'A', value: 27.8, tolerance: 0.5 }, { var: 'B', value: 2.1, tolerance: 0.3 }, { var: 'C', value: null, context: 'unpowered' },
        { var: 'D', value: 1 }, { var: 'E', value: 1 },
      ],
      [{ var: 'A', value: 27.3, tolerance: 0.5 }, { var: 'B', value: 2.5, tolerance: 0.3 }, { var: 'C', value: 1 }, { var: 'D', value: 1 }],
    );
    expect(r.match.sort()).toEqual(['A', 'D']);
    expect(r.differ).toEqual([{ var: 'B', pf: 2.1, pm: 2.5, tolerance: 0.3 }]);
  });

  it('battery volts within 0.5 V never raise a line; over it a quiet line after the hold, cleared when back within', async () => {
    pair = await startPair(sims());
    const { host, guest, server } = pair;
    const mark = host.mark();
    guest.sim.logic('N_ELEC_VOLT_BAT_1', 27.3); // the gate test 2 case: 27.8 vs 27.3
    await sleep(server.config.indicatorHoldMs * 2);
    expect(host.messages('desync', () => true, mark)).toHaveLength(0);
    // The value is not in the hash: nothing is requested for it.
    expect(host.messages('snapshot_request', () => true, mark)).toHaveLength(0);

    guest.sim.logic('N_ELEC_VOLT_BAT_1', 26.0);
    const d = await host.waitFor('desync', (m) => body(m).var === 'N_ELEC_VOLT_BAT_1', { from: mark });
    expect(body(d)).toMatchObject({ reason: 'observe_mismatch', tier: 'quiet', expected: 27.8, observed: 26 });

    // An equal group hash must not clear it (the var is not in the hash).
    await sleep(server.config.snapshotIntervalMs * 3);
    expect(host.messages('notice', (m) => body(m).var === 'N_ELEC_VOLT_BAT_1', mark)).toHaveLength(0);

    guest.sim.logic('N_ELEC_VOLT_BAT_1', 27.6);
    const cleared = await host.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === 'N_ELEC_VOLT_BAT_1', { from: mark });
    expect(body(cleared).reason).toBe('observe_mismatch');
  });

  it('an indicator of an unpowered unit is not compared, and an open line for it clears', async () => {
    pair = await startPair(sims());
    const { host, guest, server } = pair;
    const mark = host.mark();
    guest.sim.logic('I_FCU_AP1_LAMP', 1);
    await host.waitFor('desync', (m) => body(m).var === 'I_FCU_AP1_LAMP', { from: mark });

    const off = host.mark();
    guest.sim.unitPower('I_FCU_AP1_LAMP', false);
    await host.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === 'I_FCU_AP1_LAMP', { from: off });
    await sleep(server.config.indicatorHoldMs * 1.5);
    expect(host.messages('desync', (m) => body(m).var === 'I_FCU_AP1_LAMP', off)).toHaveLength(0);
  });

  it('a mode indicator of an unpowered unit is never red', async () => {
    pair = await startPair(sims());
    const { host, guest, server } = pair;
    const mark = host.mark();
    guest.sim.unitPower('B_FCU_HEADING_DASHED', false);
    host.sim.logic('B_FCU_HEADING_DASHED', 1);
    await sleep(server.config.buttonHoldMs * 3);
    expect(host.messages('desync', () => true, mark)).toHaveLength(0);
    // Powered again and still different: red, as before.
    guest.sim.unitPower('B_FCU_HEADING_DASHED', true);
    await waitUntil(() => host.messages('desync', (m) => body(m).var === 'E_FCU_HEADING' && body(m).reason === 'mode_mismatch', mark).length === 1,
      'mode line once powered', server.config.buttonHoldMs * 6);
  });
});
