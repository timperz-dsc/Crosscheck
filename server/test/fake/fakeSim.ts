import { createHash } from 'node:crypto';
import type { LooseValue, SnapshotClass, SnapshotValue } from '../../src/protocol/types.js';

export interface VarDef {
  name: string;
  cls: SnapshotClass | 'pulse';
  group: string;
  init: number | string;
  /** delta: the display var that shows the encoder result. */
  display?: string;
  displayInit?: number;
  /** pulse: character appended to `screen` on each press. */
  key?: string;
  screen?: string;
  /** pulse: effect var that toggles 0/1 on each press (unless the system is unpowered). */
  toggles?: string;
  /** owned: drift step reported in snapshot values. */
  step?: number;
  /**
   * pulse with press edge (Fenix push buttons, +1 on press and release): reported in snapshots as its
   * parity, and its toggles lamp (when not a var of its own) as a toggle item with the button.
   */
  edge?: boolean;
  /** mode: the pilots' text and the control the difference is shown under. */
  message?: string;
  reportAs?: string;
  /** delta with compare_when: reported with a context ("unpowered" with value null while the unit is off). */
  gated?: boolean;
  /** observe: compared value by value (snapshot_hash.loose) within this tolerance, not through the hash (CRO-34). */
  tolerance?: number;
  /** observe or toggle lamp: compared only while its unit is powered (unitPower); loose for observe (CRO-34). */
  powered?: boolean;
}

/** A small stand-in profile with one var of each kind. */
export const TEST_VARS: VarDef[] = [
  { name: 'S_OH_ELEC_BAT1', cls: 'verified', group: 'overhead', init: 0 },
  { name: 'S_OH_ELEC_BAT2', cls: 'verified', group: 'overhead', init: 0 },
  { name: 'S_OH_EXT_LT_STROBE', cls: 'verified', group: 'overhead', init: 0 },
  { name: 'A_OH_LIGHTING_OVD', cls: 'analog', group: 'overhead', init: 0 },
  { name: 'I_OH_ELEC_BAT1_FAULT', cls: 'observe', group: 'overhead', init: 0 },
  { name: 'E_FCU_HEADING', cls: 'delta', group: 'fcu', init: 0, display: 'N_FCU_HEADING', displayInit: 100 },
  { name: 'S_FCU_AP1', cls: 'verified', group: 'fcu', init: 0 },
  { name: 'S_CDU1_KEY_A', cls: 'pulse', group: 'mcdu', init: 0, key: 'A', screen: 'MCDU1_SCREEN' },
  { name: 'MCDU1_SCREEN', cls: 'observe', group: 'mcdu', init: '' },
  { name: 'S_FCU_EFIS1_FD', cls: 'pulse', group: 'efis', init: 0, toggles: 'I_FCU_EFIS1_FD' },
  { name: 'I_FCU_EFIS1_FD', cls: 'observe', group: 'efis', init: 1 },
  { name: 'S_FCU_EFIS2_FD', cls: 'pulse', group: 'efis', init: 0, edge: true, toggles: 'I_FCU_EFIS2_FD' },
  { name: 'S_OH_FIRE_ENG1_TEST', cls: 'pulse', group: 'overhead', init: 0, edge: true },
  {
    name: 'I_FCU_SPEED_MANAGED', cls: 'mode', group: 'fcu', init: 0, reportAs: 'S_FCU_SPEED',
    message: 'SPD managed differs between the cockpits – push or pull the knob in the cockpit that is wrong.',
  },
];

export type WriteSource = 'pilot' | 'remote' | 'correction';

/** Canonical group hash (see docs/protocol.md): sha256 over sorted "NAME=VALUE\n" lines, first 16 hex chars. */
export function canonicalHash(values: SnapshotValue[]): string {
  const lines = [...values]
    .sort((a, b) => (a.var < b.var ? -1 : a.var > b.var ? 1 : 0))
    .map((v) => `${v.var}=${formatValue(v.value)}\n`)
    .join('');
  return createHash('sha256').update(lines, 'utf8').digest('hex').slice(0, 16);
}

export function formatValue(value: number | string | null): string {
  if (value === null) return 'null';
  if (typeof value === 'string') return JSON.stringify(value);
  if (Object.is(value, -0)) return '0';
  if (Number.isInteger(value)) return String(value);
  const fixed = value.toFixed(6).replace(/0+$/, '').replace(/\.$/, '');
  return fixed === '-0' ? '0' : fixed;
}

/** In-memory simulator: var store, encoders with display vars, MCDU screen, fault injection. */
export class FakeSim {
  private readonly values = new Map<string, number | string>();
  readonly defs = new Map<string, VarDef>();
  private readonly refused = new Set<string>();
  /** Values the sim accepts per var (like the bridge's profile guard); others are refused. */
  private readonly allowed = new Map<string, number[]>();
  /** Pulses whose effect does not change (system unpowered). */
  private readonly unpowered = new Set<string>();
  private readonly stuck = new Map<string, { value: number; revertMs: number }>();
  /** Encoders (gated) whose unit is unpowered, and the mode context of the others. */
  private readonly unitOff = new Set<string>();
  private readonly modes = new Map<string, string>();
  /** Every write that reached this sim, in order. */
  readonly writes: { name: string; value: number | string; source: WriteSource }[] = [];

  constructor(defs: VarDef[] = TEST_VARS) {
    for (const d of defs) {
      this.defs.set(d.name, d);
      this.values.set(d.name, d.init);
      if (d.display) this.values.set(d.display, d.displayInit ?? 0);
    }
    for (const d of defs) if (d.toggles && !this.values.has(d.toggles)) this.values.set(d.toggles, 0);
  }

  get(name: string): number | string {
    const v = this.values.get(name);
    if (v === undefined) throw new Error(`unknown var ${name}`);
    return v;
  }

  /** Display value of an encoder. */
  display(name: string): number {
    const d = this.def(name);
    return this.get(d.display!) as number;
  }

  /** A write through SimConnect. Returns false if the sim refused it. */
  write(name: string, value: number, source: WriteSource): boolean {
    this.def(name);
    this.writes.push({ name, value, source });
    if (this.refused.has(name)) return false;
    const allowed = this.allowed.get(name);
    if (allowed && !allowed.includes(value)) return false;
    this.values.set(name, value);
    const stuck = this.stuck.get(name);
    if (stuck && value !== stuck.value) setTimeout(() => this.values.set(name, stuck.value), stuck.revertMs);
    return true;
  }

  /** The aircraft's own logic changes a value (no write, no command). */
  logic(name: string, value: number | string): void {
    if (!this.values.has(name)) throw new Error(`unknown var ${name}`);
    this.values.set(name, value);
  }

  turn(name: string, delta: number, source: WriteSource): boolean {
    const d = this.def(name);
    this.writes.push({ name, value: delta, source });
    if (this.refused.has(name)) return false;
    this.values.set(name, (this.get(name) as number) + delta);
    this.values.set(d.display!, (this.get(d.display!) as number) + delta);
    return true;
  }

  press(name: string, source: WriteSource): boolean {
    const d = this.def(name);
    this.writes.push({ name, value: 1, source });
    if (this.refused.has(name)) return false;
    if (d.screen && d.key) this.values.set(d.screen, `${this.get(d.screen) as string}${d.key}`);
    if (d.toggles && !this.unpowered.has(name)) this.values.set(d.toggles, this.get(d.toggles) === 1 ? 0 : 1);
    return true;
  }

  refuse(name: string): void {
    this.refused.add(name);
  }

  accept(name: string): void {
    this.refused.delete(name);
  }

  /** Only these values are written (others are refused), like a profile's allowed values. */
  allow(name: string, values: number[]): void {
    this.allowed.set(name, values);
  }

  /** The press has no effect (system unpowered). */
  unpower(name: string): void {
    this.unpowered.add(name);
  }

  /** Like Fenix logic that keeps changing a var back: every write reverts after revertMs. */
  stick(name: string, value: number, revertMs = 5): void {
    this.stuck.set(name, { value, revertMs });
  }

  unstick(name: string): void {
    this.stuck.delete(name);
  }

  /** The unit of a gated encoder is switched off (true) or on: its display is not comparable while off. */
  unitPower(name: string, on: boolean): void {
    if (on) this.unitOff.delete(name);
    else this.unitOff.add(name);
  }

  /** The mode of a gated encoder's unit (TRK/FPA, MACH, selected radio ...). */
  mode(name: string, context: string): void {
    this.modes.set(name, context);
  }

  groups(): string[] {
    return [...new Set([...this.defs.values()].map((d) => d.group))].sort();
  }

  snapshot(group: string): SnapshotValue[] {
    const out: SnapshotValue[] = [];
    for (const d of this.defs.values()) {
      if (d.group !== group) continue;
      if (d.cls === 'pulse' && d.edge) {
        out.push({ var: d.name, class: 'pulse', value: Math.round(this.get(d.name) as number) & 1 });
        if (d.toggles && !this.defs.has(d.toggles)) out.push({ var: d.toggles, class: 'toggle', value: this.get(d.toggles), button: d.name });
        continue;
      }
      if (d.cls === 'pulse') continue;
      if (this.isLoose(d)) continue;
      if (d.cls === 'mode' && d.powered && this.unitOff.has(d.name)) {
        out.push({ var: d.name, class: 'mode', value: null, context: 'unpowered', message: d.message!, ...(d.reportAs ? { report_as: d.reportAs } : {}) });
        continue;
      }
      if (d.cls === 'delta' && d.gated) {
        const off = this.unitOff.has(d.name);
        out.push({ var: d.name, class: 'delta', value: off ? null : this.display(d.name), context: off ? 'unpowered' : this.modes.get(d.name) ?? '' });
        continue;
      }
      out.push({
        var: d.name, class: d.cls, value: d.cls === 'delta' ? this.display(d.name) : this.get(d.name),
        ...(d.cls === 'owned' && d.step !== undefined ? { step: d.step } : {}),
        ...(d.cls === 'mode' ? { message: d.message!, ...(d.reportAs ? { report_as: d.reportAs } : {}) } : {}),
      });
    }
    return out;
  }

  hash(group: string): string {
    return canonicalHash(this.snapshot(group));
  }

  private isLoose(d: VarDef): boolean {
    return d.cls === 'observe' && (d.tolerance !== undefined || d.powered === true);
  }

  /** Loose indicators of a group (snapshot_hash.loose): tolerance or power gate, never in the hash. */
  loose(group: string): LooseValue[] {
    return [...this.defs.values()].filter((d) => d.group === group && this.isLoose(d)).map((d): LooseValue => {
      if (d.powered && this.unitOff.has(d.name)) return { var: d.name, value: null, context: 'unpowered' };
      return { var: d.name, value: this.get(d.name), ...(d.tolerance !== undefined ? { tolerance: d.tolerance } : {}) };
    });
  }

  writesBy(source: WriteSource): { name: string; value: number | string }[] {
    return this.writes.filter((w) => w.source === source);
  }

  private def(name: string): VarDef {
    const d = this.defs.get(name);
    if (!d) throw new Error(`unknown var ${name}`);
    return d;
  }
}
