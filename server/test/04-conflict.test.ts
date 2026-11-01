import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

describe('4. both pilots set the same var at once', () => {
  it('applies in seq order: the later command wins on both sims, the earlier origin is told', async () => {
    pair = await startPair({ host: { latencyMs: 5 }, guest: { latencyMs: 5 } });
    const { host, guest } = pair;

    const hostId = host.set('S_OH_EXT_LT_STROBE', 1);
    const guestId = guest.set('S_OH_EXT_LT_STROBE', 2);

    const hostSeq = (await host.waitFor('accepted', (m) => body(m).id === hostId)).seq!;
    const guestSeq = (await guest.waitFor('accepted', (m) => body(m).id === guestId)).seq!;
    const [winner, loser, winnerValue] = hostSeq > guestSeq ? [host, guest, 1] : [guest, host, 2];
    const loserId = loser === host ? hostId : guestId;

    const notice = await loser.waitFor('notice', (m) => body(m).kind === 'overridden');
    expect(body(notice)).toMatchObject({ id: loserId, var: 'S_OH_EXT_LT_STROBE', by_client_id: winner.clientId, by_role: winner.role });
    expect(body<{ message: string }>(notice).message).toContain(`overridden by ${winner.role.toUpperCase()}`);

    await waitUntil(() => host.sim.get('S_OH_EXT_LT_STROBE') === winnerValue && guest.sim.get('S_OH_EXT_LT_STROBE') === winnerValue, 'both sims on the later value');
    await sleep(150);
    expect(host.sim.get('S_OH_EXT_LT_STROBE')).toBe(winnerValue);
    expect(guest.sim.get('S_OH_EXT_LT_STROBE')).toBe(winnerValue);
    expect(winner.messages('notice', (m) => body(m).kind === 'overridden')).toHaveLength(0);
    expect([...host.messages('desync'), ...guest.messages('desync')]).toHaveLength(0);
  });

  it('commands far apart are normal use, not a conflict', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    const a = host.set('S_OH_EXT_LT_STROBE', 1);
    await host.waitFor('ack', (m) => body(m).id === a);
    await sleep(pair.server.config.conflictWindowMs + 20);
    const b = guest.set('S_OH_EXT_LT_STROBE', 2);
    await guest.waitFor('ack', (m) => body(m).id === b);
    expect(host.messages('notice', (m) => body(m).kind === 'overridden')).toHaveLength(0);
    expect(host.sim.get('S_OH_EXT_LT_STROBE')).toBe(2);
  });
});
