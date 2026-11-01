import { describe, expect, it } from 'vitest';
import { DEFAULT_CONFIG, scaleTimings } from '../src/config.js';
import { CorrectionGuard } from '../src/session/correctionGuard.js';
import { decideCorrections, diffValues, type LoggedCommand, type VarDiff } from '../src/session/corrections.js';
import { generateJoinCode, JOIN_CODE_ALPHABET, normalizeJoinCode } from '../src/session/joinCode.js';
import { comparePreflight } from '../src/session/preflight.js';
import { ReplayLog } from '../src/session/replayLog.js';
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
  it('major version compare ignores minor, patch and pre-release', () => {
    expect(comparePreflight(PREFLIGHT, { ...PREFLIGHT, bridge_version: '0.9.1-beta.2' }).rejects).toEqual([]);
    expect(comparePreflight(PREFLIGHT, { ...PREFLIGHT, bridge_version: '2.0.0' }).rejects.map((r) => r.field)).toEqual(['bridge_version']);
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
  const env = (seq: number): Envelope => ({ v: 1, type: 'command', session: 's_abcdefg', seq, ts: 0, body: {} });
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
