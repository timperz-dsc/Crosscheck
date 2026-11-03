import { isAbsolute, type DesyncReason, type LooseValue, type Role, type Scalar, type SnapshotClass, type SnapshotValue } from '../protocol/types.js';

export interface VarDiff {
  var: string;
  class: SnapshotClass;
  pf: Scalar;
  pm: Scalar;
  /** Toggle items: the edge button whose lamp differs. */
  button?: string;
  /** Mode items: the pilots' text, and the control the difference is reported under. */
  message?: string;
  report_as?: string;
  /** Delta with compare_when: each side's context ("unpowered", or its mode values). */
  pfContext?: string;
  pmContext?: string;
}

/** The latest logged pilot command for a var, as far as the safety rule needs it. */
export interface LoggedCommand {
  seq: number;
  value: Scalar;
  /** The PF's sim is known to have this value: the PF sent it, or acked it ok (not superseded). */
  pfConfirmed: boolean;
}

export type RawDecision =
  | (VarDiff & { action: 'correct'; target: number; sides: Role[]; source: 'command_log' | 'pf_reference'; source_seq?: number })
  | (VarDiff & { action: 'desync'; reason: DesyncReason })
  /** Not comparable (an encoder display while its unit is unpowered or in another mode): no decision. */
  | (VarDiff & { action: 'skip'; why: string });

const EPSILON = 1e-6;

export function sameValue(a: Scalar | undefined, b: Scalar | undefined): boolean {
  if (typeof a === 'number' && typeof b === 'number') return Math.abs(a - b) <= EPSILON;
  return a === b;
}

/** Differences between the PF's and the PM's values of one group. A var missing on one side counts as null. */
export function diffValues(pfValues: SnapshotValue[], pmValues: SnapshotValue[]): VarDiff[] {
  const pf = new Map(pfValues.map((v) => [v.var, v]));
  const pm = new Map(pmValues.map((v) => [v.var, v]));
  const names = [...new Set([...pf.keys(), ...pm.keys()])].sort();
  const out: VarDiff[] = [];
  for (const name of names) {
    const a = pf.get(name);
    const b = pm.get(name);
    if (a && b && sameValue(a.value, b.value)) continue;
    const { button, message, report_as } = (a ?? b)!;
    out.push({
      var: name, class: (a ?? b)!.class, pf: a?.value ?? null, pm: b?.value ?? null,
      ...(button ? { button } : {}), ...(message ? { message } : {}), ...(report_as ? { report_as } : {}),
      ...(a?.context !== undefined ? { pfContext: a.context } : {}), ...(b?.context !== undefined ? { pmContext: b.context } : {}),
    });
  }
  return out;
}

/** An encoder display is compared only when both sides are powered and in the same mode (compare_when). */
export function comparable(d: VarDiff): boolean {
  if (d.pfContext === 'unpowered' || d.pmContext === 'unpowered') return false;
  return (d.pfContext ?? null) === (d.pmContext ?? null);
}

/**
 * Inputs that are never corrected (protocol.md section 7): a button held down on one side only
 * (pulse, parity), a toggle lamp that differs (toggle), a mode indicator, a deny-list control
 * (denied: never written) and an encoder display (delta: never corrected, gate test 2 cause F) are
 * red desyncs for the pilots to fix, or nothing while not comparable.
 */
export function buttonDecision(d: VarDiff): RawDecision | null {
  if (d.class === 'delta') {
    if (!comparable(d)) return { ...d, action: 'skip', why: 'not_comparable' };
    if (d.pf === null || d.pm === null) return { ...d, action: 'desync', reason: 'missing_value' };
    return { ...d, action: 'desync', reason: 'display_mismatch' };
  }
  // A lamp or mode indicator of a unit that is off in either sim is not compared (CRO-34).
  if ((d.class === 'toggle' || d.class === 'mode') && !comparable(d)) return { ...d, action: 'skip', why: 'not_comparable' };
  if (d.class === 'denied') return { ...d, action: 'desync', reason: 'not_mirrored' };
  if (d.class === 'pulse') return { ...d, action: 'desync', reason: 'press_mismatch' };
  if (d.class === 'toggle') return { ...d, action: 'desync', reason: 'toggle_mismatch' };
  // An indicator of an input Crosscheck cannot sync: never written, red for the pilots to fix.
  if (d.class === 'mode') return { ...d, action: 'desync', reason: 'mode_mismatch' };
  return null;
}

/**
 * The PF safety rule. Decides per differing var what may be done. Never invents a value:
 * the target is either a logged pilot command's value or the PF's current value.
 *
 * 1. observe → desync (outputs are never written).
 * 2. Absolute class, latest logged pilot command equals one side's value and the PF never
 *    confirmed it → correct the side that differs with the command value (may be the PF).
 * 3. Otherwise → correct only the PM to the PF's value. Never write to the PF here. Absolute
 *    classes only: encoders, push buttons and deny-list controls are never corrected (buttonDecision).
 * 4. Log value matches neither side and the PF never confirmed it, or a value is unknown →
 *    desync.
 */
export function decideCorrections(diffs: VarDiff[], lastCommand: (name: string) => LoggedCommand | undefined): RawDecision[] {
  return diffs.map((d): RawDecision => {
    // Edge push buttons: never corrected, an automatic press would be a write no pilot made.
    const button = buttonDecision(d);
    if (button) return button;
    if (d.class === 'observe') return { ...d, action: 'desync', reason: 'observe_mismatch' };
    if (d.pf === null || d.pm === null) return { ...d, action: 'desync', reason: 'missing_value' };

    if (isAbsolute(d.class)) {
      const cmd = lastCommand(d.var);
      if (cmd && !cmd.pfConfirmed) {
        if (typeof cmd.value === 'number' && (sameValue(cmd.value, d.pf) || sameValue(cmd.value, d.pm))) {
          const sides: Role[] = [];
          if (!sameValue(cmd.value, d.pf)) sides.push('pf');
          if (!sameValue(cmd.value, d.pm)) sides.push('pm');
          return { ...d, action: 'correct', target: cmd.value, sides, source: 'command_log', source_seq: cmd.seq };
        }
        return { ...d, action: 'desync', reason: 'log_conflict' };
      }
    }

    if (typeof d.pf !== 'number' || !isAbsolute(d.class)) return { ...d, action: 'desync', reason: 'missing_value' };
    return { ...d, action: 'correct', target: d.pf, sides: ['pm'], source: 'pf_reference' };
  });
}

/** One loose indicator compared between the sims (CRO-34). */
export interface LooseDiff { var: string; pf: Scalar; pm: Scalar; tolerance: number | null }

/**
 * Loose indicators (a tolerance, or compared only while powered) of one group, compared value by value.
 * `differ`: over the tolerance (exact without one). `match`: comparable and within it. A var that is
 * unpowered on either side, or missing on one side, is in neither: it is not compared.
 */
export function diffLoose(pfValues: LooseValue[], pmValues: LooseValue[]): { differ: LooseDiff[]; match: string[] } {
  const pm = new Map(pmValues.map((v) => [v.var, v]));
  const differ: LooseDiff[] = [];
  const match: string[] = [];
  for (const a of pfValues) {
    const b = pm.get(a.var);
    if (!b || a.context === 'unpowered' || b.context === 'unpowered') continue;
    const tolerance = a.tolerance ?? b.tolerance ?? null;
    const within = typeof a.value === 'number' && typeof b.value === 'number'
      ? Math.abs(a.value - b.value) <= (tolerance ?? 0) + EPSILON
      : a.value === b.value;
    if (within) match.push(a.var);
    else differ.push({ var: a.var, pf: a.value, pm: b.value, tolerance });
  }
  return { differ, match };
}
