import { readFileSync } from 'node:fs';
import { afterEach, describe, expect, it } from 'vitest';
import { body, scaled, sleep, startPair, type Pair } from './fake/harness.js';
import { FakeSim, TEST_VARS, type VarDef } from './fake/fakeSim.js';

/** Anonymised extract of gate test 1 (see the description inside the file). */
interface GateTest1 {
  elec_pump: { var: string; allowed: number[]; commands: { t_ms: number; value: number }[] };
  end: { sim_disconnected_t_ms: number };
}
const DATA = JSON.parse(readFileSync(new URL('./fixtures/gate-test-1.json', import.meta.url), 'utf8')) as GateTest1;
const PUMP = DATA.elec_pump.var;
const withPump = (): VarDef[] => [...TEST_VARS, { name: PUMP, cls: 'verified', group: 'overhead', init: 0 }];

let pair: Pair;
afterEach(async () => pair?.close());

describe('21. replay of gate test 1', () => {
  it('ELEC PUMP counter values 1…28 against [0, 1]: the red desync appears and stays visible to the end', async () => {
    pair = await startPair({ host: { sim: new FakeSim(withPump()) }, guest: { sim: new FakeSim(withPump()) } });
    const { host, guest, server } = pair;
    guest.sim.allow(PUMP, DATA.elec_pump.allowed);
    const mark = [host.mark(), guest.mark()];

    // The two click series in the log are 800 s apart; the gap is shortened, the rest keeps its timing (scaled).
    const series = DATA.elec_pump.commands.map((c) => ({ ...c, t: c.t_ms > 60_000 ? c.t_ms - 800_000 : c.t_ms }));
    const start = Date.now();
    for (const c of series) {
      const wait = start + scaled(c.t) - Date.now();
      if (wait > 0) await sleep(wait);
      host.set(PUMP, c.value);
    }
    expect(series.map((c) => c.value)).toEqual(Array.from({ length: 28 }, (_, i) => i + 1));

    const first = await guest.waitFor('desync', (m) => body(m).var === PUMP, { from: mark[1] });
    expect(body(first).tier).toBe('red');
    await sleep(server.config.snapshotIntervalMs * 5);

    // Visible to the end: never cleared, every desync for it red, and still open in the session.
    const cleared = guest.messages('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === PUMP, mark[1]);
    expect(cleared).toHaveLength(0);
    expect(guest.messages('desync', (m) => body(m).var === PUMP, mark[1]).every((m) => body(m).tier === 'red')).toBe(true);
    expect(guest.sim.get(PUMP)).toBe(1);
    expect(host.sim.get(PUMP)).toBe(28);
  });

  it('the host sim disconnecting at the end: one amber line, no missing_value desyncs', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    const mark = guest.mark();
    expect(DATA.end.sim_disconnected_t_ms).toBeGreaterThan(0);
    host.setSim(false);
    host.sim.logic('S_OH_ELEC_BAT1', 1); // the sim is gone: values change or vanish
    await sleep(server.config.snapshotIntervalMs * 8);
    host.leave();
    await guest.waitFor('peer_status', (m) => body(m).state === 'left', { from: mark });

    expect(guest.messages('desync', () => true, mark)).toHaveLength(0);
    const lines = guest.messages('advisory', (m) => body(m).kind === 'sim_disconnected', mark);
    expect(lines.length).toBeGreaterThanOrEqual(1);
    expect(body(lines[0]!)).toMatchObject({ active: true, seat: 'host', tier: 'amber' });
  });
});
