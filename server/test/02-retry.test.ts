import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

describe('2. lost command → retry → ok', () => {
  it('re-sends after settle_ms + margin and gets the ack on the second attempt', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.dropCommands = 1;

    const id = host.set('S_OH_ELEC_BAT2', 1);
    const ack = await host.waitFor('ack', (m) => body(m).id === id);

    expect(guest.droppedCommands).toEqual([{ id, attempt: 0 }]);
    expect(guest.messages('command', (m) => body(m).id === id).map((m) => body(m).attempt)).toEqual([0, 1]);
    expect(body(ack)).toMatchObject({ ok: true, observed: 1, attempt: 1 });
    expect(guest.sim.get('S_OH_ELEC_BAT2')).toBe(1);

    await sleep(100);
    expect(host.messages('ack', (m) => body(m).id === id)).toHaveLength(1);
    expect([...host.messages('desync'), ...guest.messages('desync')]).toHaveLength(0);
  });

  it('never re-sends a delta step: a missing ack is a red desync after one delivery (CRO-26)', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.dropAcks = true;

    const id = host.turn('E_FCU_HEADING', 5);
    const desync = await host.waitFor('desync', (m) => body(m).command_id === id);
    expect(body(desync)).toMatchObject({ var: 'E_FCU_HEADING', reason: 'no_ack', tier: 'red' });
    expect(guest.messages('command', (m) => body(m).id === id).map((m) => body(m).attempt)).toEqual([0]);
    expect(guest.sim.display('E_FCU_HEADING')).toBe(105);
    expect(host.sim.display('E_FCU_HEADING')).toBe(105);
  });
});
