import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

type Diff = { var: string; action: string; sides?: string[]; source?: string; source_seq?: number; target?: unknown; reason?: string };
const diffFor = (name: string) => (m: { body: unknown }) => body<{ differences: Diff[] }>(m).differences.some((d) => d.var === name);
const decision = (m: { body: unknown }, name: string) => body<{ differences: Diff[] }>(m).differences.find((d) => d.var === name)!;

describe('8. snapshot mismatch → correction follows the PF safety rule', () => {
  it('PM differs, no pilot command → only the PM is corrected to the PF value', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.logic('S_OH_ELEC_BAT2', 1);

    const diff = await host.waitFor('snapshot_diff', diffFor('S_OH_ELEC_BAT2'));
    expect(decision(diff, 'S_OH_ELEC_BAT2')).toMatchObject({ action: 'correct', target: 0, sides: ['pm'], source: 'pf_reference' });
    await host.waitFor('notice', (m) => body(m).kind === 'corrected' && body(m).var === 'S_OH_ELEC_BAT2');
    expect(guest.sim.get('S_OH_ELEC_BAT2')).toBe(0);
    expect(host.sim.writesBy('correction')).toHaveLength(0);
    expect(host.messages('correction')).toHaveLength(0);
  });

  it('PF differs, no pilot command → the PF is never written; the PM follows the PF', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    host.sim.logic('S_OH_ELEC_BAT2', 1);

    const diff = await guest.waitFor('snapshot_diff', diffFor('S_OH_ELEC_BAT2'));
    expect(decision(diff, 'S_OH_ELEC_BAT2')).toMatchObject({ action: 'correct', target: 1, sides: ['pm'], source: 'pf_reference' });
    await waitUntil(() => guest.sim.get('S_OH_ELEC_BAT2') === 1, 'PM follows PF');
    expect(host.sim.get('S_OH_ELEC_BAT2')).toBe(1);
    expect(host.sim.writesBy('correction')).toHaveLength(0);
  });

  it('PF confirmed a pilot command, then its aircraft changed the value → the old command is not pushed back into the PF', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    const id = guest.set('S_FCU_AP1', 1);
    await guest.waitFor('ack', (m) => body(m).id === id); // PF (host) applied and confirmed it
    host.sim.logic('S_FCU_AP1', 0); // e.g. Fenix logic switched it off on the PF side

    const diff = await host.waitFor('snapshot_diff', diffFor('S_FCU_AP1'));
    expect(decision(diff, 'S_FCU_AP1')).toMatchObject({ action: 'correct', target: 0, sides: ['pm'], source: 'pf_reference' });
    await waitUntil(() => guest.sim.get('S_FCU_AP1') === 0, 'PM follows PF');
    expect(host.sim.writesBy('correction')).toHaveLength(0);
  });

  it('a logged pilot command the PF never received → the PF may be corrected with exactly that value', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    host.dropCommands = 4; // first send + 3 re-sends never reach the PF's sim

    const id = guest.set('S_OH_ELEC_BAT1', 1);
    const accepted = await guest.waitFor('accepted', (m) => body(m).id === id);
    await guest.waitFor('desync', (m) => body(m).command_id === id && body(m).reason === 'no_ack');

    const diff = await guest.waitFor('snapshot_diff', diffFor('S_OH_ELEC_BAT1'));
    expect(decision(diff, 'S_OH_ELEC_BAT1')).toMatchObject({ action: 'correct', target: 1, sides: ['pf'], source: 'command_log', source_seq: accepted.seq });
    const correction = await host.waitFor('correction', (m) => body(m).var === 'S_OH_ELEC_BAT1');
    expect(body(correction)).toMatchObject({ target: 1, source: 'command_log', source_seq: accepted.seq });
    await waitUntil(() => host.sim.get('S_OH_ELEC_BAT1') === 1, 'PF corrected from the log');
    expect(host.sim.writesBy('correction')).toEqual([{ name: 'S_OH_ELEC_BAT1', value: 1, source: 'correction' }]);
    // The open desync from the lost command closes once the correction is confirmed.
    await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === 'S_OH_ELEC_BAT1');
  });

  it('neither side has the logged value → no correction at all, a desync for both', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    host.dropCommands = 4;
    const id = guest.set('S_OH_ELEC_BAT1', 1);
    await guest.waitFor('desync', (m) => body(m).command_id === id);
    guest.sim.logic('S_OH_ELEC_BAT1', 2);

    const desync = await host.waitFor('desync', (m) => body(m).reason === 'log_conflict');
    expect(body(desync)).toMatchObject({ var: 'S_OH_ELEC_BAT1', values: { pf: 0, pm: 2 } });
    await guest.waitFor('desync', (m) => body(m).reason === 'log_conflict');
    await sleep(300);
    expect([...host.messages('correction'), ...guest.messages('correction')]).toHaveLength(0);
    expect(host.sim.get('S_OH_ELEC_BAT1')).toBe(0);
  });

  it('an output (observe) differs → desync once, never written, stays open without repeating', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 1);

    const desync = await host.waitFor('desync', (m) => body(m).var === 'I_OH_ELEC_BAT1_FAULT');
    expect(body(desync)).toMatchObject({ reason: 'observe_mismatch', values: { pf: 0, pm: 1 } });
    await sleep(500);
    expect(host.messages('desync', (m) => body(m).var === 'I_OH_ELEC_BAT1_FAULT')).toHaveLength(1);
    expect([...host.messages('correction'), ...guest.messages('correction')]).toHaveLength(0);

    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 0);
    const cleared = await host.waitFor('notice', (m) => body(m).kind === 'desync_cleared');
    expect(body(cleared)).toMatchObject({ var: 'I_OH_ELEC_BAT1_FAULT', reason: 'observe_mismatch' });
  });
});
