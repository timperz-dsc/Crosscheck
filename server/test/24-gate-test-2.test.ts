import { readFileSync } from 'node:fs';
import { afterEach, describe, expect, it } from 'vitest';
import { FakeSim, TEST_VARS, type VarDef } from './fake/fakeSim.js';
import { body, scaled, sleep, startPair, type Pair } from './fake/harness.js';

// CRO-33: replays from gate test 2 (docs/gate-tests/CRO-22-gate-test-2.md), anonymised extract.
const data = JSON.parse(readFileSync(new URL('./fixtures/gate-test-2.json', import.meta.url), 'utf8')) as {
  fd_burst: { var: string; lamp: string; edges: [number, 'down' | 'up', number][] };
  idg: { var: string; values: [number, number][] };
  alt: { var: string; display: string; unpowered: { pf: number; pm: number }; powered: { pf: number; pm: number }; written_clicks: number[] };
  apu_reset: { var: string; start_value: number; end_parity_in_gate_test: number };
};

const IDG = data.idg.var;
const ALT = data.alt.var;
const APU = data.apu_reset.var;
const FD = data.fd_burst.var;
const FD_LAMP = data.fd_burst.lamp;

/** The test profile plus the gate test 2 controls: a deny-list button, a gated encoder and two push buttons. */
const VARS: VarDef[] = [
  ...TEST_VARS.filter((d) => d.name !== FD && d.name !== FD_LAMP),
  { name: IDG, cls: 'denied', group: 'overhead', init: 0 },
  { name: ALT, cls: 'delta', group: 'fcu', init: 0, display: data.alt.display, displayInit: 100, gated: true },
  { name: APU, cls: 'pulse', group: 'overhead', init: 0, edge: true },
  { name: FD, cls: 'pulse', group: 'efis', init: 0, edge: true, toggles: FD_LAMP },
];

let pair: Pair;
afterEach(async () => pair?.close());

function sims(host: Partial<Record<string, number>> = {}, guest: Partial<Record<string, number>> = {}): { host: FakeSim; guest: FakeSim } {
  const make = (init: Partial<Record<string, number>>) => {
    const sim = new FakeSim(VARS);
    for (const [name, value] of Object.entries(init)) sim.logic(name, value!);
    return sim;
  };
  return { host: make(host), guest: make(guest) };
}

async function start(host: Partial<Record<string, number>> = {}, guest: Partial<Record<string, number>> = {}, server = {}): Promise<Pair> {
  const s = sims(host, guest);
  pair = await startPair({ server, host: { sim: s.host }, guest: { sim: s.guest } });
  return pair;
}

describe('24. gate test 2 replays (CRO-33)', () => {
  it('an IDG click is never mirrored: one red "not mirrored" line naming the host cockpit, never corrected', async () => {
    const { host, guest } = await start();
    for (const [, value] of data.idg.values.slice(0, 4)) host.sim.logic(IDG, value); // the host bridge never sends these
    host.send('denied_input', { var: IDG, group: 'overhead', label: 'IDG 1' });
    for (const b of [host, guest]) {
      const d = await b.waitFor('desync', (m) => body(m).var === IDG);
      expect(body(d)).toMatchObject({ reason: 'not_mirrored', tier: 'red', client_id: host.clientId });
      expect(body(d).message).toBe('IDG 1 pressed in the host cockpit – not mirrored (irreversible). Do the same in the other cockpit if intended.');
    }
    await sleep(scaled(8000)); // snapshot rounds: 4 vs 0 stays visible, nothing written
    expect(guest.messages('correction')).toHaveLength(0);
    expect(host.messages('correction')).toHaveLength(0);
    expect(guest.messages('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === IDG)).toHaveLength(0);
    // The guest does the same: the values match, the line clears.
    guest.sim.logic(IDG, 4);
    await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === IDG);
  });

  it('a pilot who joins after IDG was pressed still gets the red line from the snapshot', async () => {
    const { host, guest } = await start({ [IDG]: 2 });
    const d = await guest.waitFor('desync', (m) => body(m).var === IDG);
    expect(body(d)).toMatchObject({ reason: 'not_mirrored', tier: 'red', client_id: host.clientId, values: { pf: 2, pm: 0 } });
    expect(guest.messages('correction')).toHaveLength(0);
    await host.waitFor('desync', (m) => body(m).var === IDG);
  });

  it('ALT, guest FCU unpowered: nothing written and no line; both powered: no write and exactly one red line', async () => {
    const { host, guest } = await start();
    host.sim.logic(data.alt.display, data.alt.unpowered.pf);
    guest.sim.logic(data.alt.display, data.alt.unpowered.pm);
    guest.sim.unitPower(ALT, false);
    await sleep(scaled(10_000));
    expect(guest.messages('correction')).toHaveLength(0); // gate test 2: +900 clicks six times, then a red loop
    expect(guest.messages('desync', (m) => body(m).var === ALT)).toHaveLength(0);
    expect(pair.server.logLines().some((l) => l.event === 'snapshot_not_compared' && JSON.stringify(l).includes(ALT))).toBe(true);

    guest.sim.unitPower(ALT, true);
    host.sim.logic(data.alt.display, data.alt.powered.pf);
    guest.sim.logic(data.alt.display, data.alt.powered.pm);
    const d = await guest.waitFor('desync', (m) => body(m).var === ALT);
    expect(body(d)).toMatchObject({ reason: 'display_mismatch', tier: 'red', values: { pf: data.alt.powered.pf, pm: data.alt.powered.pm } });
    await sleep(scaled(10_000));
    expect(guest.messages('desync', (m) => body(m).var === ALT)).toHaveLength(1);
    expect(guest.messages('correction')).toHaveLength(0);
    expect(guest.sim.writesBy('correction')).toHaveLength(0);
    // Different modes (e.g. one RMP on HF): not comparable either; the open line stays until the values match.
    guest.sim.logic(data.alt.display, data.alt.powered.pf);
    await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === ALT);
  });

  it('a display that differs only while the knob is being turned raises nothing', async () => {
    const { host, guest } = await start();
    guest.sim.logic(data.alt.display, 101); // differs by one step (the aircraft, not a lost click)
    for (let i = 0; i < 8; i++) {
      host.turn(ALT, 1); // the host keeps turning: every click is applied on both sides
      await sleep(scaled(1000));
    }
    expect(guest.messages('desync', (m) => body(m).var === ALT)).toHaveLength(0); // still turning: no red per click
    const d = await guest.waitFor('desync', (m) => body(m).var === ALT && body(m).reason === 'display_mismatch');
    expect(body(d)).toMatchObject({ tier: 'red', values: { pf: 108, pm: 109 } });
    expect(guest.messages('correction')).toHaveLength(0);
  });

  it('two pilots on one counter button: stuck pressed in the guest cockpit, cleared when released', async () => {
    // As gate test 2 left it: the guest's button rests odd, the host's is up.
    const start_ = data.apu_reset.start_value;
    const { host, guest } = await start({ [APU]: start_ + 2 }, { [APU]: start_ + 5 });
    expect(data.apu_reset.end_parity_in_gate_test).toBe(1);
    const d = await guest.waitFor('desync', (m) => body(m).var === APU && body(m).reason === 'stuck_pressed');
    expect(body(d)).toMatchObject({ tier: 'red', client_id: guest.clientId, values: { pf: 0, pm: 1 } });
    expect(body(d).message).toContain('stuck pressed in the guest cockpit');
    await host.waitFor('desync', (m) => body(m).var === APU && body(m).reason === 'stuck_pressed');
    expect(guest.messages('correction')).toHaveLength(0); // never released automatically
    // "Release it" in the guest's cockpit: one up step.
    guest.sim.logic(APU, start_ + 6);
    await guest.waitFor('notice', (m) => body(m).kind === 'desync_cleared' && body(m).var === APU);
  });

  it('a lost press is red; a suspected counter and an encoder click proven by its display are not', async () => {
    const { host, guest } = await start();
    guest.dropAcks = true;
    host.send('command', { id: 'h-lost', var: FD, class: 'pulse', value: 1, group: 'efis', settle_ms: 20, edge: 'down', gap_ms: 41 });
    await guest.waitFor('command', (m) => body(m).id === 'h-lost');
    guest.send('ack', { id: 'h-lost', ok: false, observed: 43, reason: 'press_lost' });
    const lost = await host.waitFor('desync', (m) => body(m).var === FD);
    expect(body(lost)).toMatchObject({ reason: 'press_lost', tier: 'red', client_id: guest.clientId });

    host.send('command', { id: 'h-alt', var: ALT, class: 'delta', delta: 1, group: 'fcu', settle_ms: 20 });
    await guest.waitFor('command', (m) => body(m).id === 'h-alt');
    guest.send('ack', { id: 'h-alt', ok: false, observed: 100, reason: 'display_check' });
    await sleep(scaled(1000));
    expect(host.messages('desync', (m) => body(m).var === ALT && body(m).reason === 'refused')).toHaveLength(0);

    host.send('command', { id: 'h-bat', var: 'S_OH_ELEC_BAT1', class: 'verified', value: 3, group: 'overhead', settle_ms: 20 });
    await guest.waitFor('command', (m) => body(m).id === 'h-bat');
    guest.send('ack', { id: 'h-bat', ok: false, observed: 1, reason: 'not_synced' });
    await sleep(scaled(1000));
    expect(host.messages('desync', (m) => body(m).var === 'S_OH_ELEC_BAT1')).toHaveLength(0);
  });

  it('a suspected counter is one amber line for both pilots and is never corrected', async () => {
    const { host, guest } = await start();
    host.send('counter_suspect', { var: 'S_OH_ELEC_BAT1', group: 'overhead', label: 'BAT 1' });
    for (const b of [host, guest]) {
      const a = await b.waitFor('advisory', (m) => body(m).kind === 'counter_suspect');
      expect(body(a)).toMatchObject({ key: 'counter_suspect:S_OH_ELEC_BAT1', tier: 'amber', active: true, seat: 'host' });
    }
    host.sim.logic('S_OH_ELEC_BAT1', 7);
    await sleep(scaled(10_000));
    expect(guest.messages('correction')).toHaveLength(0);
    expect(guest.messages('desync', (m) => body(m).var === 'S_OH_ELEC_BAT1')).toHaveLength(0);
  });

  it('FD burst with 41 ms gaps: one lamp check after the burst, no flicker per press', async () => {
    const { host, guest } = await start({}, {}, { effectSettleMs: 400, ackMarginMs: 300 });
    const t0 = Date.now();
    const downs: string[] = [];
    for (const [t, edge, gap] of data.fd_burst.edges.filter(([t]) => t < 1500)) {
      const wait = t0 + t - Date.now();
      if (wait > 0) await sleep(wait);
      const id = host.nextId();
      if (edge === 'down') {
        host.sim.press(FD, 'pilot');
        downs.push(id);
      }
      host.send('command', {
        id, var: FD, class: 'pulse', value: edge === 'down' ? 1 : 0, group: 'efis', settle_ms: 150, edge, gap_ms: gap,
        ...(edge === 'down' ? { effect: { var: FD_LAMP, expect: 'same' } } : {}),
      });
    }
    // The host reads its lamp once, 400 ms after its last edge, for the burst's last down edge.
    await sleep(400);
    host.send('effect_report', { id: downs.at(-1)!, var: FD_LAMP, value: host.sim.get(FD_LAMP) });
    await sleep(1500);
    expect(host.messages('desync', (m) => body(m).reason === 'effect_mismatch')).toHaveLength(0);
    expect(guest.sim.get(FD_LAMP)).toBe(host.sim.get(FD_LAMP));
    const checks = pair.server.logLines().filter((l) => l.event === 'effect_ok' || l.event === 'effect_check_superseded');
    expect(checks.filter((l) => l.event === 'effect_ok')).toHaveLength(1);
    expect(checks.filter((l) => l.event === 'effect_check_superseded').length).toBe(downs.length - 1);
  });

  it('FD burst: a lamp that still differs after the burst is one red effect_mismatch', async () => {
    const { host } = await start({}, {}, { effectSettleMs: 400, ackMarginMs: 300 });
    const ids: string[] = [];
    for (let i = 0; i < 3; i++) {
      const id = host.nextId();
      ids.push(id);
      host.send('command', { id, var: FD, class: 'pulse', value: 1, group: 'efis', settle_ms: 150, edge: 'down', gap_ms: 60, effect: { var: FD_LAMP, expect: 'same' } });
      host.send('command', { id: host.nextId(), var: FD, class: 'pulse', value: 0, group: 'efis', settle_ms: 150, edge: 'up', gap_ms: 41 });
      await sleep(60);
    }
    await sleep(400);
    host.send('effect_report', { id: ids.at(-1)!, var: FD_LAMP, value: 7 }); // differs from the guest's lamp
    const d = await host.waitFor('desync', (m) => body(m).reason === 'effect_mismatch');
    expect(body(d)).toMatchObject({ var: FD, command_id: ids.at(-1) });
    await sleep(500);
    expect(host.messages('desync', (m) => body(m).reason === 'effect_mismatch')).toHaveLength(1);
  });
});

