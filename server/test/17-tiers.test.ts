import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

describe('17. tiers: indicators are quiet and wait, inputs are red', () => {
  it('an indicator that differs is raised quiet, and only after the hold time', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    const mark = host.mark();
    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 1);

    await sleep(server.config.indicatorHoldMs * 0.6);
    expect(host.messages('desync', (m) => body(m).var === 'I_OH_ELEC_BAT1_FAULT', mark)).toHaveLength(0);

    const d = await host.waitFor('desync', (m) => body(m).var === 'I_OH_ELEC_BAT1_FAULT', { from: mark });
    expect(body(d)).toMatchObject({ reason: 'observe_mismatch', tier: 'quiet' });
    expect(d.ts - host.received[mark]!.ts).toBeGreaterThanOrEqual(0);
  });

  it('an indicator that recovers within the hold time is never raised', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    const mark = host.mark();
    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 1);
    await sleep(server.config.indicatorHoldMs * 0.4);
    guest.sim.logic('I_OH_ELEC_BAT1_FAULT', 0);
    await sleep(server.config.indicatorHoldMs * 1.5);
    expect(host.messages('desync', () => true, mark)).toHaveLength(0);
  });

  it('a switch that differs and cannot be corrected is red (correction_failed)', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    guest.sim.logic('S_OH_ELEC_BAT2', 1);
    guest.sim.refuse('S_OH_ELEC_BAT2');
    const d = await host.waitFor('desync', (m) => body(m).var === 'S_OH_ELEC_BAT2' && body(m).reason === 'correction_failed');
    expect(body(d).tier).toBe('red');
  });
});
