import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

const cleared = (p: Pair, name: string, from = 0) =>
  p.host.messages('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === name, from);

describe('16. an input desync stays until it is resolved (cause G)', () => {
  it('more refused clicks never wipe the red desync', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    // Like ELEC PUMP in gate test 1: a press counter (1, 2, 3 ...) against a profile that allows [0, 1].
    guest.sim.allow('S_OH_ELEC_BAT1', [0, 1]);
    host.set('S_OH_ELEC_BAT1', 1);
    await sleep(40);

    const first = host.set('S_OH_ELEC_BAT1', 2);
    const d = await host.waitFor('desync', (m) => body(m).command_id === first);
    expect(body(d)).toMatchObject({ var: 'S_OH_ELEC_BAT1', reason: 'refused', tier: 'red' });

    // The pilot keeps clicking: each new command is refused too.
    for (const v of [3, 4, 5]) {
      host.set('S_OH_ELEC_BAT1', v);
      await sleep(40);
    }
    await sleep(200);
    expect(cleared(pair, 'S_OH_ELEC_BAT1')).toHaveLength(0);
    // It may move from refused to correction_failed (the PM correction is refused too), but it stays red and open.
    const all = host.messages('desync', (m) => body(m).var === 'S_OH_ELEC_BAT1');
    expect(all.length).toBeGreaterThanOrEqual(1);
    expect(all.every((m) => body(m).tier === 'red')).toBe(true);
  });

  it('an ok ack for a new command on the var clears it', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.refuse('S_OH_ELEC_BAT1');
    const first = host.set('S_OH_ELEC_BAT1', 1);
    await host.waitFor('desync', (m) => body(m).command_id === first);

    guest.sim.accept('S_OH_ELEC_BAT1');
    host.set('S_OH_ELEC_BAT1', 1);
    const n = await host.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === 'S_OH_ELEC_BAT1');
    expect(body<{ message: string }>(n).message).toContain('a command for it was confirmed');
  });

  it('matching values in a snapshot clear it', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.refuse('S_OH_ELEC_BAT1');
    const first = host.set('S_OH_ELEC_BAT1', 1);
    await host.waitFor('desync', (m) => body(m).command_id === first);

    guest.sim.logic('S_OH_ELEC_BAT1', 1); // the guest set it by hand
    const n = await host.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === 'S_OH_ELEC_BAT1');
    expect(body<{ message: string }>(n).message).toContain('values match again');
  });

  it('a failed key press is never cleared by a snapshot (keys are not in snapshots)', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    guest.sim.refuse('S_CDU1_KEY_A');
    const id = host.press('S_CDU1_KEY_A');
    const d = await host.waitFor('desync', (m) => body(m).command_id === id);
    expect(body(d)).toMatchObject({ reason: 'refused', tier: 'red' });
    await sleep(server.config.snapshotIntervalMs * 6);
    expect(cleared(pair, 'S_CDU1_KEY_A')).toHaveLength(0);
  });

  it('a late ok ack after the desync still clears it', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.dropAcks = true;
    const id = host.press('S_CDU1_KEY_A');
    await host.waitFor('desync', (m) => body(m).command_id === id && body(m).reason === 'no_ack');
    guest.dropAcks = false;
    guest.send('ack', { id, ok: true, observed: 1 });
    await waitUntil(() => cleared(pair, 'S_CDU1_KEY_A').length === 1, 'late ack clears');
  });
});
