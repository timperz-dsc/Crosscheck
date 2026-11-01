import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

interface Changed {
  kind: string;
  pf_client_id: string;
  confirmed: string[];
  differences: { groups: string[]; vars: string[] };
  desyncs: { var: string }[];
  last_frame: { frame: number; from: string } | null;
}

describe('9. control transfer (real time, like a real cockpit)', () => {
  it('is granted immediately while snapshots differ; the differences are listed in control_changed', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 1);
    await guest.waitFor('desync', (m) => body(m).var === 'I_OH_ELEC_BAT1_FAULT');
    host.stream(41, { lat: 47.46, lon: 8.55 });
    host.stream(42, { lat: 47.47, lon: 8.55 });
    await guest.waitFor('stream', (m) => body(m).frame === 42);

    const from = [host.mark(), guest.mark()] as const;
    guest.requestControl();
    const handoverH = await host.waitFor('control_handover', () => true, { from: from[0] });
    const handoverG = await guest.waitFor('control_handover', () => true, { from: from[1] });
    expect(body(handoverH)).toMatchObject({
      kind: 'take', new_pf_client_id: guest.clientId, previous_pf_client_id: host.clientId, freeze_ms: server.config.freezeMs,
    });
    expect(handoverG.seq).toBe(handoverH.seq);

    const changed = await host.waitFor('control_changed', () => true, { from: from[0] });
    await guest.waitFor('control_changed', () => true, { from: from[1] });
    const c = body<Changed>(changed);
    expect(c).toMatchObject({ kind: 'take', pf_client_id: guest.clientId, pf_seat: 'guest', confirmed: [] });
    expect(c.differences.groups).toContain('overhead');
    expect(c.differences.vars).toContain('I_OH_ELEC_BAT1_FAULT');
    expect(c.desyncs.map((d) => d.var)).toEqual(['I_OH_ELEC_BAT1_FAULT']);
    expect(c.last_frame).toMatchObject({ frame: 42, from: host.clientId });
    expect(guest.messages('control_denied')).toHaveLength(0);
    expect(guest.role).toBe('pf');
    expect(host.role).toBe('pm');
    await waitUntil(() => server.logLines().some((l) => l.event === 'control_acked'), 'both control acks');
    expect(host.messages('notice', (m) => body(m).kind === 'control_ack_missing')).toHaveLength(0);
  });

  it('the pilot flying can hand control to the pilot monitoring ("You have control")', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;

    guest.giveControl(); // the PM cannot give what it does not have
    expect(body(await guest.waitFor('control_denied'))).toMatchObject({ reason: 'not_pf' });

    host.giveControl();
    const handover = await guest.waitFor('control_handover');
    expect(body(handover)).toMatchObject({ kind: 'give', new_pf_client_id: guest.clientId, previous_pf_client_id: host.clientId });
    expect(host.messages('control_changed')).toHaveLength(0); // the freeze comes first
    const changed = await host.waitFor('control_changed');
    expect(body<Changed>(changed)).toMatchObject({ kind: 'give', pf_client_id: guest.clientId, confirmed: [], last_frame: null });
    await waitUntil(() => guest.role === 'pf' && host.role === 'pm', 'roles swapped');
    await waitUntil(() => server.logLines().some((l) => l.event === 'control_acked'), 'both control acks');

    // And back again: the new PF gives it back.
    const mark = host.mark();
    guest.giveControl();
    const back = await host.waitFor('control_changed', () => true, { from: mark });
    expect(body(back)).toMatchObject({ kind: 'give', pf_client_id: host.clientId, previous_pf_client_id: guest.clientId });
  });

  it('the pilot flying cannot request control again; a missing control ack is reported', async () => {
    pair = await startPair();
    const { host, guest } = pair;
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

  it('a second request during the freeze is refused', async () => {
    pair = await startPair({ server: { freezeMs: 200 } });
    const { host, guest } = pair;
    guest.requestControl();
    await host.waitFor('control_handover');
    host.giveControl();
    expect(body(await host.waitFor('control_denied'))).toMatchObject({ reason: 'handover_in_progress' });
    await host.waitFor('control_changed');
    expect(host.messages('control_handover')).toHaveLength(1);
  });

  it('if the pilot flying is lost, the pilot monitoring takes control only with an explicit confirm', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    host.disconnect();
    await guest.waitFor('peer_status', (m) => body(m).state === 'lost');

    guest.requestControl();
    expect(body(await guest.waitFor('control_denied'))).toMatchObject({ reason: 'peer_lost' });
    expect(guest.role).toBe('pm');

    guest.requestControl(['@peer_lost']);
    const changed = await guest.waitFor('control_changed');
    expect(body(changed)).toMatchObject({ kind: 'take', pf_client_id: guest.clientId, confirmed: ['@peer_lost'] });
  });

  it('the pilot flying cannot give control to a pilot who is not connected', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.disconnect();
    await host.waitFor('peer_status', (m) => body(m).state === 'lost');
    host.giveControl();
    expect(body(await host.waitFor('control_denied'))).toMatchObject({ reason: 'peer_lost' });
    await sleep(pair.server.config.freezeMs * 2);
    expect(host.messages('control_handover')).toHaveLength(0);
    expect(host.role).toBe('pf');
  });
});
