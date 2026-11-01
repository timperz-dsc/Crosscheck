import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

interface Advisory { key: string; kind: string; active: boolean; tier: string; seat: string; var?: string; label?: string; count?: number; message: string }
const adv = (m: { body: unknown }) => body<Advisory>(m);

describe('18. advisories (amber lines)', () => {
  it('a disconnected sim is one amber line, not hundreds of missing_value desyncs', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    const mark = [host.mark(), guest.mark()];
    host.setSim(false);

    const a = await guest.waitFor('advisory', (m) => adv(m).kind === 'sim_disconnected', { from: mark[1] });
    expect(adv(a)).toMatchObject({ key: 'sim_disconnected:host', active: true, tier: 'amber', seat: 'host' });
    await host.waitFor('advisory', (m) => adv(m).kind === 'sim_disconnected', { from: mark[0] });

    // Values change on the connected side; nothing is compared meanwhile.
    guest.sim.logic('S_OH_ELEC_BAT1', 1);
    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 1);
    await sleep(server.config.snapshotIntervalMs * 8);
    expect(guest.messages('desync', () => true, mark[1])).toHaveLength(0);
    expect(guest.messages('snapshot_request', () => true, mark[1])).toHaveLength(0);

    const back = guest.mark();
    host.setSim(true);
    const off = await guest.waitFor('advisory', (m) => adv(m).kind === 'sim_disconnected' && !adv(m).active, { from: back });
    expect(adv(off).key).toBe('sim_disconnected:host');
    const req = await guest.waitFor('snapshot_request', () => true, { from: back });
    expect(body(req).reason).toBe('resync');
  });

  it('a control outside the profile is one line per control for both pilots, kept in resume', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    host.unsyncedInput('S_FCU_AP1_X', 'AP1');
    const a = await guest.waitFor('advisory', (m) => adv(m).kind === 'not_synced');
    expect(adv(a)).toMatchObject({ key: 'not_synced:S_FCU_AP1_X', active: true, tier: 'amber', seat: 'host', var: 'S_FCU_AP1_X', label: 'AP1', count: 1 });
    await host.waitFor('advisory', (m) => adv(m).kind === 'not_synced');

    guest.unsyncedInput('S_FCU_AP1_X', 'AP1');
    const again = await host.waitFor('advisory', (m) => adv(m).count === 2);
    expect(adv(again)).toMatchObject({ key: 'not_synced:S_FCU_AP1_X', seat: 'guest' });

    const resumed = await guest.resume();
    expect(body<{ advisories: Advisory[] }>(resumed).advisories).toEqual([expect.objectContaining({ key: 'not_synced:S_FCU_AP1_X', count: 2 })]);
  });

  it('three overrides within the window: "hardware fighting", gone after a quiet window', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    // Both pilots' hardware keeps setting the parking brake (conflict window is short in tests: send back to back).
    for (let i = 0; i < 4; i++) {
      host.set('S_OH_EXT_LT_STROBE', 1);
      guest.set('S_OH_EXT_LT_STROBE', 0);
      await sleep(5);
    }
    const a = await host.waitFor('advisory', (m) => adv(m).kind === 'hardware_fighting' && adv(m).active);
    expect(adv(a)).toMatchObject({ key: 'hardware_fighting:S_OH_EXT_LT_STROBE', tier: 'amber', var: 'S_OH_EXT_LT_STROBE' });
    expect(adv(a).count).toBeGreaterThanOrEqual(server.config.fightCount);
    await guest.waitFor('advisory', (m) => adv(m).kind === 'hardware_fighting' && adv(m).active);

    await waitUntil(() => host.messages('advisory', (m) => adv(m).kind === 'hardware_fighting' && !adv(m).active).length === 1,
      'fighting advisory ends', server.config.fightWindowMs * 3);
  });

  it('two overrides are not fighting yet', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    // Each switch to the other pilot's command is one override: 1 → 0 → 1 is two.
    host.set('S_OH_EXT_LT_STROBE', 1);
    guest.set('S_OH_EXT_LT_STROBE', 0);
    host.set('S_OH_EXT_LT_STROBE', 1);
    await sleep(server.config.fightWindowMs / 3);
    const overrides = [...host.messages('notice', (m) => body(m).kind === 'overridden'), ...guest.messages('notice', (m) => body(m).kind === 'overridden')];
    expect(overrides.length).toBeGreaterThan(0);
    expect(overrides.length).toBeLessThan(server.config.fightCount);
    expect(host.messages('advisory', (m) => adv(m).kind === 'hardware_fighting')).toHaveLength(0);
  });
});
