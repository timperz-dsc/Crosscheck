import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { DEFAULT_CONFIG, scaleTimings } from '../src/config.js';
import { CorrectionGuard } from '../src/session/correctionGuard.js';
import { decideCorrections, diffValues, type LoggedCommand, type VarDiff } from '../src/session/corrections.js';
import { generateJoinCode, JOIN_CODE_ALPHABET, normalizeJoinCode } from '../src/session/joinCode.js';
import { comparePreflight } from '../src/session/preflight.js';
import { ReplayLog } from '../src/session/replayLog.js';
import { driftShouldSend, effectsMatch, tierFor } from '../src/session/session.js';
import type { Envelope } from '../src/protocol/types.js';
import { PREFLIGHT } from './fake/fakeBridge.js';
import { canonicalHash, formatValue } from './fake/fakeSim.js';

describe('default timings (protocol v1)', () => {
  it('heartbeat 1 s, lost after 5 s, session kept 5 min, snapshots every 2 s, 3 re-sends', () => {
    expect(DEFAULT_CONFIG).toMatchObject({
      heartbeatMs: 1000, lostAfterMs: 5000, sessionTtlMs: 300_000, snapshotIntervalMs: 2000, maxRetries: 3,
      correctionLoopMax: 3, correctionLoopWindowMs: 60_000, streamMaxHz: 30,
    });
  });
  it('scaling changes timings only', () => {
    const s = scaleTimings(DEFAULT_CONFIG, 0.05);
    expect(s).toMatchObject({ heartbeatMs: 50, lostAfterMs: 250, sessionTtlMs: 15_000, maxRetries: 3, logSize: 2000 });
  });
});

describe('PF safety rule (decideCorrections)', () => {
  const d = (over: Partial<VarDiff> = {}): VarDiff => ({ var: 'X', class: 'verified', pf: 0, pm: 1, ...over });
  const log = (cmd?: LoggedCommand) => () => cmd;

  it('no command → PM corrected to PF value', () => {
    expect(decideCorrections([d()], log())).toEqual([{ ...d(), action: 'correct', target: 0, sides: ['pm'], source: 'pf_reference' }]);
  });
  it('command the PF never confirmed, PM has it → PF corrected from the log', () => {
    const [r] = decideCorrections([d()], log({ seq: 9, value: 1, pfConfirmed: false }));
    expect(r).toMatchObject({ action: 'correct', target: 1, sides: ['pf'], source: 'command_log', source_seq: 9 });
  });
  it('command the PF never confirmed, PF already has it → PM corrected from the log', () => {
    const [r] = decideCorrections([d()], log({ seq: 9, value: 0, pfConfirmed: false }));
    expect(r).toMatchObject({ action: 'correct', target: 0, sides: ['pm'], source: 'command_log' });
  });
  it('command the PF confirmed → PF is the reference, PF never written', () => {
    const [r] = decideCorrections([d()], log({ seq: 9, value: 1, pfConfirmed: true }));
    expect(r).toMatchObject({ action: 'correct', target: 0, sides: ['pm'], source: 'pf_reference' });
  });
  it('log value on neither side → desync, no correction', () => {
    const [r] = decideCorrections([d()], log({ seq: 9, value: 2, pfConfirmed: false }));
    expect(r).toMatchObject({ action: 'desync', reason: 'log_conflict' });
  });
  it('observe → desync; unknown value → desync', () => {
    expect(decideCorrections([d({ class: 'observe' })], log())[0]).toMatchObject({ action: 'desync', reason: 'observe_mismatch' });
    expect(decideCorrections([d({ pf: null })], log())[0]).toMatchObject({ action: 'desync', reason: 'missing_value' });
  });
  it('delta (display value) → PM gets the PF display as target, never the PF', () => {
    const [r] = decideCorrections([d({ class: 'delta', pf: 120, pm: 118 })], log({ seq: 3, value: 5, pfConfirmed: false }));
    expect(r).toMatchObject({ action: 'correct', target: 120, sides: ['pm'], source: 'pf_reference' });
  });
  it('never targets the PF with a value that is not a logged command', () => {
    const cases: [VarDiff, LoggedCommand | undefined][] = [
      [d(), undefined], [d({ class: 'analog', pf: 0.2, pm: 0.7 }), { seq: 1, value: 0.2, pfConfirmed: true }],
      [d({ class: 'owned', pf: 1000, pm: 900 }), undefined], [d(), { seq: 2, value: 1, pfConfirmed: false }],
    ];
    for (const [diff, cmd] of cases) {
      for (const r of decideCorrections([diff], log(cmd))) {
        if (r.action === 'correct' && r.sides.includes('pf')) {
          expect(r.source).toBe('command_log');
          expect(r.target).toBe(cmd!.value);
        }
      }
    }
  });
  it('diffValues: missing var on one side counts as null, small float noise is equal', () => {
    const diffs = diffValues(
      [{ var: 'A', class: 'analog', value: 0.5 }, { var: 'B', class: 'verified', value: 1 }],
      [{ var: 'A', class: 'analog', value: 0.5000001 }],
    );
    expect(diffs).toEqual([{ var: 'B', class: 'verified', pf: 1, pm: null }]);
  });
});

describe('correction loop guard', () => {
  it('allows max corrections per window, then refuses until the window passes or it is reset', () => {
    let now = 0;
    const g = new CorrectionGuard(3, 60_000, () => now);
    for (let i = 0; i < 3; i++) {
      expect(g.allows('X')).toBe(true);
      g.record('X');
      now += 1000;
    }
    expect(g.allows('X')).toBe(false);
    expect(g.allows('Y')).toBe(true);
    now = 60_001;
    expect(g.allows('X')).toBe(true); // first correction left the window
    g.record('X');
    g.record('X');
    expect(g.allows('X')).toBe(false);
    g.reset('X');
    expect(g.allows('X')).toBe(true);
  });
});

describe('preflight', () => {
  it('same preflight → nothing', () => {
    expect(comparePreflight(PREFLIGHT, PREFLIGHT)).toEqual({ rejects: [], warnings: [] });
  });
  it('major version compare: patch and pre-release may differ; before 1.0 the minor counts as the major', () => {
    const ref = { ...PREFLIGHT, bridge_version: '0.3.0' };
    expect(comparePreflight(ref, { ...ref, bridge_version: '0.3.5-beta.2' }).rejects).toEqual([]);
    expect(comparePreflight(ref, { ...ref, bridge_version: '0.1.0' }).rejects.map((r) => r.field)).toEqual(['bridge_version']);
    expect(comparePreflight(ref, { ...ref, bridge_version: '0.2.0' }).rejects.map((r) => r.field)).toEqual(['bridge_version']);
    expect(comparePreflight(ref, { ...ref, bridge_version: '0.4.0' }).rejects.map((r) => r.field)).toEqual(['bridge_version']);
    expect(comparePreflight(ref, { ...ref, bridge_version: '2.0.0' }).rejects.map((r) => r.field)).toEqual(['bridge_version']);
    const one = { ...PREFLIGHT, bridge_version: '1.2.0' };
    expect(comparePreflight(one, { ...one, bridge_version: '1.3.4' }).rejects).toEqual([]);
  });
  it('profile id mismatch is rejected', () => {
    expect(comparePreflight(PREFLIGHT, { ...PREFLIGHT, profile_id: 'other' }).rejects.map((r) => r.field)).toEqual(['profile_id']);
  });
});

describe('join codes', () => {
  it('6 characters from an alphabet without look-alikes', () => {
    for (const ch of '0O1IL') expect(JOIN_CODE_ALPHABET).not.toContain(ch);
    for (let i = 0; i < 200; i++) {
      const code = generateJoinCode();
      expect(code).toMatch(/^[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{6}$/);
    }
  });
  it('normalizes case and rejects impossible codes', () => {
    expect(normalizeJoinCode(' k7m3qx ')).toBe('K7M3QX');
    expect(normalizeJoinCode('K7M3Q0')).toBeNull();
    expect(normalizeJoinCode('K7M3Q')).toBeNull();
  });
});

describe('replay log', () => {
  const env = (seq: number): Envelope => ({ v: 3, type: 'command', session: 's_abcdefg', seq, ts: 0, body: {} });
  it('replays per client after last_seq and reports a gap once entries were evicted', () => {
    const log = new ReplayLog(3);
    log.add(1, 'a', env(1));
    log.add(1, 'b', env(1));
    log.add(2, 'b', env(2));
    log.add(3, 'a', env(3));
    expect(log.replayFor('a', 0)?.map((m) => m.seq)).toEqual([1, 3]);
    log.add(4, 'b', env(4)); // evicts seq 1 (for a and b)
    expect(log.replayFor('a', 0)).toBeNull();
    expect(log.replayFor('a', 1)?.map((m) => m.seq)).toEqual([3]);
    expect(log.replayFor('b', 2)?.map((m) => m.seq)).toEqual([4]);
  });
});

describe('canonical snapshot hash (fake bridge, documented in docs/protocol.md)', () => {
  it('formats numbers stably and ignores order', () => {
    expect(formatValue(1)).toBe('1');
    expect(formatValue(-0)).toBe('0');
    expect(formatValue(0.1 + 0.2)).toBe('0.3');
    expect(formatValue(0.5)).toBe('0.5');
    expect(formatValue('A B')).toBe('"A B"');
    const a = canonicalHash([{ var: 'B', class: 'verified', value: 1 }, { var: 'A', class: 'analog', value: 0.25 }]);
    const b = canonicalHash([{ var: 'A', class: 'analog', value: 0.25 }, { var: 'B', class: 'verified', value: 1 }]);
    expect(a).toBe(b);
    expect(a).toMatch(/^[0-9a-f]{16}$/);
  });
});

describe('canonical hash test vectors (protocol/test-vectors, also checked by the C# bridge)', () => {
  it('matches every vector', () => {
    const file = new URL('../../protocol/test-vectors/canonical-hash.json', import.meta.url);
    const { cases } = JSON.parse(readFileSync(file, 'utf8')) as { cases: { values: { var: string; value: number | string | null }[]; formatted: string[]; hash: string }[] };
    expect(cases.length).toBeGreaterThan(5);
    for (const c of cases) {
      const values = c.values.map((v) => ({ var: v.var, class: 'verified' as const, value: v.value }));
      expect(values.map((v) => formatValue(v.value))).toEqual(c.formatted);
      expect(canonicalHash(values)).toBe(c.hash);
    }
  });
});

describe('desync tiers (protocol/test-vectors/desync-tiers.json, also checked by the C# bridge)', () => {
  it('only an indicator may be quiet; every failed pilot input is red', () => {
    const file = new URL('../../protocol/test-vectors/desync-tiers.json', import.meta.url);
    const { cases } = JSON.parse(readFileSync(file, 'utf8')) as { cases: { name: string; class: string; delta_display: boolean; tier: string }[] };
    // The server never sees a delta display as observe: the bridge reports it under the encoder (class delta).
    for (const c of cases) expect(tierFor(c.delta_display ? 'delta' : c.class), c.name).toBe(c.tier);
    for (const cls of ['verified', 'analog', 'delta', 'pulse']) expect(tierFor(cls)).toBe('red');
  });
});

describe('pulse effect comparison', () => {
  it('same compares values, parity compares odd/even', () => {
    expect(effectsMatch(1, 1, 'same')).toBe(true);
    expect(effectsMatch(0, 1, 'same')).toBe(false);
    expect(effectsMatch(3, 5, 'parity')).toBe(true);
    expect(effectsMatch(3, 4, 'parity')).toBe(false);
    expect(effectsMatch('ab', 'ab', 'same')).toBe(true);
  });
});

describe('drift rate (protocol/test-vectors/drift-rate.json)', () => {
  interface Case { name: string; step: number | null; min_interval_ms: number; steps: { t_ms: number; drift: Record<string, number>; send: boolean }[] }
  it('matches every vector', () => {
    const file = new URL('../../protocol/test-vectors/drift-rate.json', import.meta.url);
    const { cases } = JSON.parse(readFileSync(file, 'utf8')) as { cases: Case[] };
    for (const c of cases) {
      let sent = new Map<string, number>();
      let sentAt = 0;
      for (const s of c.steps) {
        const current = new Map(Object.entries(s.drift));
        const send = driftShouldSend(sent, current, sentAt, s.t_ms, () => c.step ?? undefined, c.min_interval_ms);
        expect(send, `${c.name} at ${s.t_ms}`).toBe(s.send);
        if (send) {
          sent = current;
          sentAt = s.t_ms;
        }
      }
    }
  });

  it('the gate test 1 fuel drift (1,289 messages) shrinks to a handful', () => {
    const file = new URL('./fixtures/gate-test-1.json', import.meta.url);
    const { fuel_drift: fuel } = JSON.parse(readFileSync(file, 'utf8')) as { fuel_drift: { step: number; messages: [number, number | null][] } };
    expect(fuel.messages).toHaveLength(1289);
    let sent = new Map<string, number>();
    let sentAt = 0;
    let count = 0;
    for (const [t, delta] of fuel.messages) {
      const current = new Map(delta === null ? [] : [['fuel', delta] as [string, number]]);
      if (!driftShouldSend(sent, current, sentAt, t, () => fuel.step, 10_000)) continue;
      count++;
      sent = current;
      sentAt = t;
    }
    // Every start, stop and jump of the difference is still sent; the repeats are not.
    expect(count).toBeLessThan(40);
    expect(count).toBeGreaterThan(0);
  });
});
