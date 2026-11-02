import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

const TEST = 'S_OH_FIRE_ENG1_TEST';
const FD2 = 'S_FCU_EFIS2_FD';
const FD2_LAMP = 'I_FCU_EFIS2_FD';

/** Server log events of one kind that mention a var (snapshot comparisons). */
function heldFor(pair: Pair, name: string): number {
  return pair.server.logLines().filter((l) => l.event === 'snapshot_diff_held' && JSON.stringify(l).includes(name)).length;
}

describe('23. edge push buttons (CRO-29/30): edges are relayed, button state is never corrected', () => {
  it('an edge command is relayed with its edge and gap', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    host.send('command', { id: 'h-e1', var: TEST, class: 'pulse', value: 1, group: 'overhead', settle_ms: 20, edge: 'down', gap_ms: 0 });
    host.send('command', { id: 'h-e2', var: TEST, class: 'pulse', value: 0, group: 'overhead', settle_ms: 20, edge: 'up', gap_ms: 2100 });
    const down = await guest.waitFor('command', (m) => body(m).id === 'h-e1');
    const up = await guest.waitFor('command', (m) => body(m).id === 'h-e2');
    expect(body(down)).toMatchObject({ edge: 'down', gap_ms: 0, class: 'pulse' });
    expect(body(up)).toMatchObject({ edge: 'up', gap_ms: 2100 });
    expect(up.seq!).toBeGreaterThan(down.seq!);
  });

  it('a button held down on one side only is a red press_mismatch on both sides and is never corrected', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    host.sim.logic(TEST, 7); // odd: held down in the host's cockpit only
    const d = await guest.waitFor('desync', (m) => body(m).reason === 'press_mismatch');
    expect(body(d)).toMatchObject({ var: TEST, tier: 'red', values: { pf: 1, pm: 0 } });
    await host.waitFor('desync', (m) => body(m).reason === 'press_mismatch');
    expect(host.messages('correction')).toHaveLength(0);
    expect(guest.messages('correction')).toHaveLength(0);
    // Released: both up again → cleared.
    host.sim.logic(TEST, 8);
    await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === TEST);
  });

  it('a single mismatching comparison (a snapshot taken mid-mirror) raises nothing; the second one does', async () => {
    pair = await startPair({ server: { buttonHoldMs: 60_000 } });
    const { host, guest } = pair;
    host.sim.logic(TEST, 1);
    await waitUntil(() => heldFor(pair, TEST) >= 1, 'first comparison held');
    expect(guest.messages('desync', (m) => body(m).var === TEST)).toHaveLength(0);
    await guest.waitFor('desync', (m) => body(m).reason === 'press_mismatch');
  });

  it('a match in between starts the count again', async () => {
    pair = await startPair({ server: { buttonHoldMs: 60_000 } });
    const { host, guest } = pair;
    host.sim.logic(TEST, 1);
    await waitUntil(() => heldFor(pair, TEST) >= 1, 'first comparison held');
    host.sim.logic(TEST, 2); // released before the second comparison
    guest.sim.logic(TEST, 2);
    await sleep(pair.server.config.snapshotIntervalMs * 4);
    const before = heldFor(pair, TEST);
    host.sim.logic(TEST, 3);
    await waitUntil(() => heldFor(pair, TEST) > before, 'held again after the match');
    expect(guest.messages('desync', (m) => body(m).var === TEST)).toHaveLength(0);
  });

  it('a toggle lamp that differs is a red toggle_mismatch under its button, never pressed automatically', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    host.sim.logic(FD2_LAMP, 1); // e.g. the guest joined with FD off
    const d = await guest.waitFor('desync', (m) => body(m).reason === 'toggle_mismatch');
    expect(body(d)).toMatchObject({ var: FD2, tier: 'red', values: { pf: 1, pm: 0 } });
    expect(String(body(d).message)).toMatch(/If nothing above it differs, press it on one side/);
    expect(host.messages('correction')).toHaveLength(0);
    expect(guest.messages('correction')).toHaveLength(0);
    expect(guest.sim.writesBy('correction')).toHaveLength(0);
    // A pilot presses FD on one side: the lamps match → cleared.
    guest.sim.logic(FD2_LAMP, 1);
    await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === FD2);
  });

  it('an indicator of an unsynced input (mode) is red under its control after the second comparison, with the profile text, never corrected', async () => {
    pair = await startPair({ server: { buttonHoldMs: 60_000 } });
    const { host, guest } = pair;
    host.sim.logic('I_FCU_SPEED_MANAGED', 1); // SPD pushed in the host's cockpit only
    await waitUntil(() => heldFor(pair, 'I_FCU_SPEED_MANAGED') >= 1, 'first comparison held');
    expect(guest.messages('desync', (m) => body(m).reason === 'mode_mismatch')).toHaveLength(0);
    const d = await guest.waitFor('desync', (m) => body(m).reason === 'mode_mismatch');
    expect(body(d)).toMatchObject({ var: 'S_FCU_SPEED', tier: 'red', values: { pf: 1, pm: 0 } });
    expect(String(body(d).message)).toBe('SPD managed differs between the cockpits – push or pull the knob in the cockpit that is wrong.');
    expect(host.messages('correction')).toHaveLength(0);
    expect(guest.sim.writesBy('correction')).toHaveLength(0);
    // The pilot fixes it in the cockpit that is wrong: the lamps match → cleared.
    guest.sim.logic('I_FCU_SPEED_MANAGED', 1);
    await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === 'S_FCU_SPEED');
  });

  it('safety_release is a red desync to both pilots and clears when the button state matches', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.send('safety_release', { var: TEST, group: 'overhead', cause: 'cap', observed: 8 });
    const d = await host.waitFor('desync', (m) => body(m).reason === 'safety_release');
    expect(body(d)).toMatchObject({ var: TEST, tier: 'red', observed: 8 });
    expect(String(body(d).message)).toMatch(/Crosscheck released this button in the guest's cockpit because no release arrived within 30 s/);
    await guest.waitFor('desync', (m) => body(m).reason === 'safety_release');
    // Both sims show the button up: the next matching snapshot clears it.
    await host.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === TEST);
  });
});
