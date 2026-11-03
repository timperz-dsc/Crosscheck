import { afterEach, describe, expect, it } from 'vitest';
import { FakeSim, TEST_VARS, type VarDef } from './fake/fakeSim.js';
import { body, sleep, startPair, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

// CRO-40 (gate test 3): a var SimConnect refuses to write is one red line for both pilots, never silent; the
// server never sends a correction outside the profile's range or values; an indicator with a profile notice
// (rudder trim position) is one amber line with that text.
const NOTICE = 'Rudder trim differs – press RESET or adjust it.';
const EXTRA: VarDef[] = [
  { name: 'A_FC_TRIM_KNOB', cls: 'analog', group: 'flightcontrols', init: 0, range: [0, 1] },
  { name: 'S_EXT_SELECTOR', cls: 'verified', group: 'flightcontrols', init: 0, values: [0, 1] },
  { name: 'N_FC_RUDDER_TRIM_DECIMAL', cls: 'observe', group: 'flightcontrols', init: 0, tolerance: 1, notice: NOTICE },
];
const sims = () => ({ host: { sim: new FakeSim([...TEST_VARS, ...EXTRA]) }, guest: { sim: new FakeSim([...TEST_VARS, ...EXTRA]) } });

interface Advisory { key: string; kind: string; active: boolean; tier: string; var?: string; message: string }

describe('26. cannot write, corrections inside the profile, amber notices (CRO-40)', () => {
  it('write_blocked is one red cannot_write line for both pilots that stays, and no correction is sent for that var', async () => {
    pair = await startPair(sims());
    const { host, guest, server } = pair;
    const mark = [host.mark(), guest.mark()];
    guest.send('write_blocked', { var: 'S_OH_EXT_LT_STROBE', group: 'overhead', label: 'STROBE' });
    for (const [b, m] of [[host, mark[0]], [guest, mark[1]]] as const) {
      const d = await b.waitFor('desync', (x) => body(x).var === 'S_OH_EXT_LT_STROBE', { from: m });
      expect(body(d)).toMatchObject({ reason: 'cannot_write', tier: 'red', client_id: guest.clientId });
      expect(body<{ message: string }>(d).message).toBe('Crosscheck cannot write STROBE in the guest cockpit. Set it by hand in both cockpits.');
    }
    // The PF's value changes with no command (a snapshot difference): never corrected into the guest's sim.
    host.sim.logic('S_OH_EXT_LT_STROBE', 1);
    await sleep(server.config.snapshotIntervalMs * 6);
    expect(guest.messages('correction', (x) => body(x).var === 'S_OH_EXT_LT_STROBE', mark[1])).toHaveLength(0);
    // Still open (it never clears by itself while the cockpits differ).
    expect(host.messages('notice', (x) => body(x).kind === 'desync_cleared' && body(x).var === 'S_OH_EXT_LT_STROBE', mark[0])).toHaveLength(0);
  });

  it('an ack with reason cannot_write fails at once: no re-send, one red cannot_write line', async () => {
    pair = await startPair(sims());
    const { host, guest } = pair;
    guest.cannotWrite.add('S_OH_ELEC_BAT2');
    const mark = host.mark();
    const id = host.set('S_OH_ELEC_BAT2', 1);
    const d = await host.waitFor('desync', (x) => body(x).var === 'S_OH_ELEC_BAT2', { from: mark });
    expect(body(d)).toMatchObject({ reason: 'cannot_write', tier: 'red' });
    expect(guest.messages('command', (x) => body(x).id === id).map((x) => body(x).attempt)).toEqual([0]);
    expect(guest.sim.get('S_OH_ELEC_BAT2')).toBe(0);
  });

  it('never a correction outside the profile range or values: a red out_of_range line instead', async () => {
    pair = await startPair(sims());
    const { host, guest, server } = pair;
    const mark = [host.mark(), guest.mark()];
    // Gate test 3: the PF's trim knob read 5400 with a profile range of [0, 1].
    host.sim.logic('A_FC_TRIM_KNOB', 5400);
    host.sim.logic('S_EXT_SELECTOR', 2);
    const trim = await host.waitFor('desync', (x) => body(x).var === 'A_FC_TRIM_KNOB', { from: mark[0] });
    expect(body(trim)).toMatchObject({ reason: 'out_of_range', tier: 'red', expected: 5400, observed: 0 });
    const selector = await host.waitFor('desync', (x) => body(x).var === 'S_EXT_SELECTOR', { from: mark[0] });
    expect(body(selector)).toMatchObject({ reason: 'out_of_range', tier: 'red' });
    expect(guest.messages('correction', (x) => ['A_FC_TRIM_KNOB', 'S_EXT_SELECTOR'].includes(body<{ var: string }>(x).var), mark[1])).toHaveLength(0);

    // The red line blocks corrections while it is open, like every red line: the pilots set it by hand, and it
    // clears when the cockpits match.
    host.sim.logic('A_FC_TRIM_KNOB', 0.5);
    await sleep(server.config.snapshotIntervalMs * 4);
    expect(guest.messages('correction', (x) => body(x).var === 'A_FC_TRIM_KNOB', mark[1])).toHaveLength(0);
    const fixed = host.mark();
    guest.sim.logic('A_FC_TRIM_KNOB', 0.5);
    await host.waitFor('notice', (x) => body(x).kind === 'desync_cleared' && body(x).var === 'A_FC_TRIM_KNOB', { from: fixed });
  });

  it('a difference inside the profile limits is still corrected to the pilot flying', async () => {
    pair = await startPair(sims());
    const { host, guest } = pair;
    const mark = guest.mark();
    host.sim.logic('A_FC_TRIM_KNOB', 0.5);
    const c = await guest.waitFor('correction', (x) => body(x).var === 'A_FC_TRIM_KNOB', { from: mark });
    expect(body(c)).toMatchObject({ target: 0.5, source: 'pf_reference' });
  });

  it('an indicator with a notice is one amber line with its text beyond the tolerance, never a desync, cleared when within it', async () => {
    pair = await startPair(sims());
    const { host, guest, server } = pair;
    const mark = [host.mark(), guest.mark()];
    const name = 'N_FC_RUDDER_TRIM_DECIMAL';
    guest.sim.logic(name, 1); // one step: within the tolerance
    await sleep(server.config.indicatorHoldMs * 2);
    expect(host.messages('advisory', (x) => body<Advisory>(x).var === name, mark[0])).toHaveLength(0);

    guest.sim.logic(name, -5);
    for (const [b, m] of [[host, mark[0]], [guest, mark[1]]] as const) {
      const a = await b.waitFor('advisory', (x) => body<Advisory>(x).kind === 'indicator_differs', { from: m });
      expect(body<Advisory>(a)).toMatchObject({ key: `indicator:${name}`, active: true, tier: 'amber', var: name, message: NOTICE });
    }
    await sleep(server.config.snapshotIntervalMs * 3);
    expect(host.messages('desync', (x) => body(x).var === name, mark[0])).toHaveLength(0);
    expect(host.messages('advisory', (x) => body<Advisory>(x).kind === 'indicator_differs', mark[0])).toHaveLength(1); // once, not every round

    const back = host.mark();
    guest.sim.logic(name, 0.5);
    const off = await host.waitFor('advisory', (x) => body<Advisory>(x).kind === 'indicator_differs' && !body<Advisory>(x).active, { from: back });
    expect(body<Advisory>(off).key).toBe(`indicator:${name}`);
  });
});
