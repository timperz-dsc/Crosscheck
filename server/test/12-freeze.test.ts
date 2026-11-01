import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

describe('12. no corrections during a control handover freeze', () => {
  it('defers a mismatch found during the freeze and corrects it afterwards against the new PF', async () => {
    // Hashes are sent by hand so the mismatch lands inside the freeze window.
    pair = await startPair({ server: { freezeMs: 400 }, host: { autoHash: false }, guest: { autoHash: false } });
    const { host, guest } = pair;
    guest.sim.logic('S_OH_ELEC_BAT2', 1);

    host.sendHashes();
    await sleep(10);
    guest.sendHashes(); // first comparable mismatch
    await sleep(20);

    guest.requestControl();
    await host.waitFor('control_handover');
    host.sendHashes();
    await sleep(10);
    guest.sendHashes(); // second mismatch → values requested while frozen

    const diff = await host.waitFor('snapshot_diff');
    expect(body<{ differences: unknown[] }>(diff).differences).toEqual([
      expect.objectContaining({ var: 'S_OH_ELEC_BAT2', action: 'deferred' }),
    ]);
    expect([...host.messages('correction'), ...guest.messages('correction')]).toHaveLength(0);
    expect(host.messages('control_changed')).toHaveLength(0);

    // After the handover the guest is PF; the host (now PM) is corrected to the guest's value.
    await host.waitFor('control_changed');
    const correction = await host.waitFor('correction');
    expect(body(correction)).toMatchObject({ var: 'S_OH_ELEC_BAT2', target: 1, source: 'pf_reference' });
    const changedAt = host.received.findIndex((m) => m.type === 'control_changed');
    expect(host.received.indexOf(correction)).toBeGreaterThan(changedAt);
    await waitUntil(() => host.sim.get('S_OH_ELEC_BAT2') === 1, 'host corrected');
    expect(guest.sim.writesBy('correction')).toHaveLength(0);
  });
});
