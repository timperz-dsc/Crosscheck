import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

describe('9. control transfer', () => {
  it('is refused while snapshots differ and granted once they match', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 1);
    await guest.waitFor('desync', (m) => body(m).var === 'I_OH_ELEC_BAT1_FAULT');

    guest.requestControl();
    const denied = await guest.waitFor('control_denied');
    expect(body(denied)).toMatchObject({ reason: 'snapshot_mismatch', differences: ['overhead'] });
    expect(body<{ desyncs: { var: string }[] }>(denied).desyncs.map((d) => d.var)).toEqual(['I_OH_ELEC_BAT1_FAULT']);
    expect(guest.role).toBe('pm');

    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 0);
    await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared');
    // Like a real client: ask again until the server has fresh, matching hashes from both sides.
    const from = [host.mark(), guest.mark()] as const;
    for (let i = 0; i < 40; i++) {
      const mark = guest.mark();
      guest.requestControl();
      const answer = await guest.waitForAny(['control_denied', 'control_handover'], { from: mark });
      if (answer.type === 'control_handover') break;
      expect(body(answer).reason).toBe('snapshot_mismatch');
      await sleep(25);
    }

    const handoverH = await host.waitFor('control_handover', () => true, { from: from[0] });
    const handoverG = await guest.waitFor('control_handover', () => true, { from: from[1] });
    expect(body(handoverH)).toMatchObject({ new_pf_client_id: guest.clientId, previous_pf_client_id: host.clientId, freeze_ms: server.config.freezeMs });
    expect(handoverG.seq).toBe(handoverH.seq);

    const changed = await host.waitFor('control_changed', () => true, { from: from[0] });
    await guest.waitFor('control_changed', () => true, { from: from[1] });
    expect(body(changed)).toMatchObject({ pf_client_id: guest.clientId, pf_seat: 'guest', confirmed: [] });
    expect(guest.role).toBe('pf');
    expect(host.role).toBe('pm');
    await waitUntil(() => server.logLines().some((l) => l.event === 'control_acked'), 'both control acks');
    expect(host.messages('notice', (m) => body(m).kind === 'control_ack_missing')).toHaveLength(0);
  });

  it('can be taken anyway when the requester confirms exactly the listed differences', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 1);
    await guest.waitFor('desync', (m) => body(m).var === 'I_OH_ELEC_BAT1_FAULT');

    guest.requestControl(['fcu']); // wrong list → still refused
    const denied = await guest.waitFor('control_denied');
    expect(body(denied).differences).toEqual(['overhead']);

    guest.requestControl(['overhead']);
    const changed = await host.waitFor('control_changed');
    expect(body(changed)).toMatchObject({ pf_client_id: guest.clientId, confirmed: ['overhead'] });
  });

  it('the pilot flying cannot request control again; a missing control ack is reported', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    await sleep(pair.server.config.snapshotIntervalMs * 2); // both sides have reported equal hashes
    host.requestControl();
    expect(body(await host.waitFor('control_denied')).reason).toBe('already_pf');

    // The host's bridge does not confirm the change (its outgoing messages are dropped for a moment).
    guest.requestControl();
    await host.waitFor('control_handover');
    host.mute = true;
    const notice = await guest.waitFor('notice', (m) => body(m).kind === 'control_ack_missing');
    expect(body(notice).client_id).toBe(host.clientId);
    host.mute = false;
  });
});
