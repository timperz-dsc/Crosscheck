import { afterEach, describe, expect, it } from 'vitest';
import { body, scaled, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

const groupsMatch = (p: Pair) => p.host.sim.groups().every((g) => p.host.sim.hash(g) === p.guest.sim.hash(g));

describe('6. disconnect for 10 s → resume', () => {
  it('replays the missed commands from the log; sims match afterwards', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    const seqBefore = guest.lastSeq;

    guest.disconnect();
    await host.waitFor('peer_status', (m) => body(m).state === 'lost');
    const ids = [
      host.set('S_OH_ELEC_BAT1', 1),
      host.set('S_OH_EXT_LT_STROBE', 2),
      host.turn('E_FCU_HEADING', 7),
      host.press('S_CDU1_KEY_A'),
    ];
    await sleep(scaled(10_000));
    // Nothing was re-sent into the void and no desync was raised while the guest was away.
    expect(host.messages('desync')).toHaveLength(0);

    const resumed = await guest.resume();
    expect(resumed.type).toBe('resumed');
    expect(body(resumed)).toMatchObject({ full_resync: false, replayed: 4, role: 'pm' });
    const replayed = guest.received.filter((m) => m.replay);
    expect(replayed.map((m) => body(m).id)).toEqual(ids);
    expect(replayed.every((m) => m.seq! > seqBefore)).toBe(true);

    await waitUntil(() => ids.every((id) => host.messages('ack', (m) => body(m).id === id).length === 1), 'acks for all missed commands');
    await host.waitFor('peer_status', (m) => body(m).state === 'resumed');
    expect(groupsMatch(pair)).toBe(true);
    expect(guest.sim.get('MCDU1_SCREEN')).toBe('A');
    expect(guest.sim.display('E_FCU_HEADING')).toBe(107);
    expect([...host.messages('desync'), ...guest.messages('desync')]).toHaveLength(0);
  });

  it('log already overwritten → full snapshot resync for all groups', async () => {
    pair = await startPair({ server: { logSize: 3 } });
    const { host, guest } = pair;

    guest.disconnect();
    await host.waitFor('peer_status', (m) => body(m).state === 'lost');
    host.set('S_OH_ELEC_BAT1', 1);
    host.set('S_OH_ELEC_BAT2', 1);
    host.set('S_FCU_AP1', 1);
    host.turn('E_FCU_HEADING', -10);
    host.press('S_CDU1_KEY_A');
    await sleep(scaled(10_000));

    const from = guest.mark();
    const resumed = await guest.resume();
    expect(body(resumed)).toMatchObject({ full_resync: true, replayed: 0 });
    await guest.waitFor('snapshot_request', (m) => body(m).reason === 'resync' && body(m).groups === null, { from });

    // Switches and the encoder are corrected on the PM side to the PF's values...
    await waitUntil(() => ['overhead', 'fcu'].every((g) => host.sim.hash(g) === guest.sim.hash(g)), 'overhead and fcu match');
    expect(guest.sim.display('E_FCU_HEADING')).toBe(90);
    expect(host.sim.writesBy('correction')).toHaveLength(0);
    // ...but a missed key press cannot be replayed: the MCDU difference is a visible desync.
    const desync = await guest.waitFor('desync', (m) => body(m).var === 'MCDU1_SCREEN');
    expect(body(desync)).toMatchObject({ reason: 'observe_mismatch', values: { pf: 'A', pm: '' } });
  });

  it('a slot is kept for the session TTL only; resuming later fails', async () => {
    pair = await startPair({ server: { sessionTtlMs: 150 } });
    const { host, guest } = pair;
    guest.disconnect();
    await host.waitFor('peer_status', (m) => body(m).state === 'lost');
    await host.waitFor('peer_status', (m) => body(m).state === 'expired');
    const answer = await guest.resume();
    expect(answer.type).toBe('error');
    expect(body(answer).code).toBe('resume_failed');
  });
});
