import { afterEach, describe, expect, it } from 'vitest';
import { body, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

describe('1. basic command → ack', () => {
  it('relays a switch command in seq order and forwards the ack to the origin', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;

    const id = host.set('S_OH_ELEC_BAT1', 1);
    const accepted = await host.waitFor('accepted', (m) => body(m).id === id);
    const relayed = await guest.waitFor('command', (m) => body(m).id === id);
    const ack = await host.waitFor('ack', (m) => body(m).id === id);

    expect(relayed.seq).toBe(accepted.seq);
    expect(body(relayed).origin).toEqual({ client_id: host.clientId, seat: 'host', role: 'pf' });
    expect(body(ack)).toMatchObject({ id, ok: true, observed: 1, from: guest.clientId, attempt: 0 });
    expect(guest.sim.get('S_OH_ELEC_BAT1')).toBe(1);
    expect(host.messages('desync')).toHaveLength(0);

    // Logged in the session JSONL: command in, relay out, ack in, ack out.
    const lines = server.logLines();
    const forId = lines.filter((l) => (l.msg as { body?: { id?: string } } | undefined)?.body?.id === id);
    expect(forId.map((l) => `${l.dir}:${(l.msg as { type: string }).type}`)).toEqual(
      expect.arrayContaining(['in:command', 'out:command', 'out:accepted', 'in:ack', 'out:ack']),
    );
  });

  it('works the other way round and for MCDU key pulses', async () => {
    pair = await startPair();
    const { host, guest } = pair;

    const id = guest.press('S_CDU1_KEY_A');
    await guest.waitFor('ack', (m) => body(m).id === id);
    expect(host.sim.get('MCDU1_SCREEN')).toBe('A');
    expect(guest.sim.get('MCDU1_SCREEN')).toBe('A');

    // Fast typing: every press arrives, none is dropped or merged.
    const ids = [guest.press('S_CDU1_KEY_A'), host.press('S_CDU1_KEY_A'), guest.press('S_CDU1_KEY_A')];
    await waitUntil(() => ids.every((i) => [...host.received, ...guest.received].some((m) => m.type === 'ack' && body(m).id === i)), 'all key acks');
    expect(host.sim.get('MCDU1_SCREEN')).toBe('AAAA');
    expect(guest.sim.get('MCDU1_SCREEN')).toBe('AAAA');
  });
});
