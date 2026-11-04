// The sentences the UI shows. Plain, specific, sentence case.
import type { AmberItem, ControlNotice, CrewMember, LineAction, OwnedRow, PanelItem, PanelLine, PreflightRow, Side, Status } from './types';

export const sideName = (side: Side) => (side === 'captain' ? 'captain' : 'first officer');
export const SideName = (side: Side) => (side === 'captain' ? 'Captain' : 'First officer');
export const roleName = (role: 'pf' | 'pm') => (role === 'pf' ? 'Pilot flying' : 'Pilot monitoring');

/** The other seat, from my own seat side. */
export const otherSide = (s: Status): Side => (s.seat_side === 'fo' ? 'captain' : 'fo');

export function timeOfDay(iso: string): string {
  const d = new Date(iso);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

export function secondsSince(iso: string, now: number): number {
  return Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
}

export function lastCompared(iso: string | null | undefined, now: number): string {
  if (!iso) return 'Not compared yet.';
  const s = secondsSince(iso, now);
  return s <= 1 ? (s === 0 ? 'Last compared just now.' : 'Last compared 1 second ago.') : `Last compared ${s} seconds ago.`;
}

const NUMBERS = ['No', 'One', 'Two', 'Three', 'Four', 'Five', 'Six', 'Seven', 'Eight', 'Nine'];
export function differsHeading(n: number): string {
  if (n === 0) return 'All controls match';
  const count = NUMBERS[n] ?? String(n);
  return n === 1 ? `${count} control differs` : `${count} controls differ`;
}

export function serverText(s: Status): string {
  if (s.server === 'connected') return s.rtt_ms != null ? `Connected, ${Math.round(s.rtt_ms)} ms` : 'Connected';
  if (s.server === 'reconnecting') return 'Reconnecting';
  return 'Not connected';
}

/** The sim in the status bar: "MSFS 2024 connected". */
export function simText(s: Status): string {
  const sim = s.this_computer.sim;
  return sim.connected ? `${sim.name} connected` : `${sim.name} not connected`;
}

export function aircraftText(s: Status): string {
  const a = s.this_computer.aircraft;
  if (a.reading) return `${a.name}, reading the version`;
  if (!a.app_version || a.app_version === 'unknown') return `${a.name}, version unknown`;
  return `${a.name}, app ${a.app_version}`;
}

export function profileText(s: Status): string {
  const p = s.this_computer.profile;
  return `${p.id}, made for ${p.builds.join(', ')}`;
}

export function peerLostText(peer: CrewMember, lostAt: string, ttlMs: number, now: number): string {
  const secs = secondsSince(lostAt, now);
  const minutes = Math.round(ttlMs / 60000);
  const since = secs < 60 ? `${secs} s` : `${Math.floor(secs / 60)} min`;
  return `The ${sideName(peer.side)} lost connection ${since} ago. The session stays open for ${minutes} ${minutes === 1 ? 'minute' : 'minutes'}.`;
}

// ---- preflight ----

export function preflightResult(row: PreflightRow): string {
  if (row.result === 'same') return 'Same';
  if (row.result === 'warn' && (row.you === 'unknown' || row.other === 'unknown')) return 'Unknown';
  return 'Different';
}

function whereUnknown(row: PreflightRow, other: string): string {
  if (row.you === 'unknown' && row.other === 'unknown') return 'on either computer';
  return row.you === 'unknown' ? 'on your computer' : `on the ${other}'s computer`;
}

/** What a difference means and what to do. other = "captain". */
export function preflightExplanation(row: PreflightRow, other: string): string | null {
  if (row.result === 'same') return null;
  switch (row.field) {
    case 'airac':
      return row.you === 'unknown' || row.other === 'unknown'
        ? `The AIRAC cycle is entered by hand and was not entered ${whereUnknown(row, other)}, so it cannot be compared. You can join. Check that both pilots use the same AIRAC before you build the flight plan.`
        : 'The navigation data differs, so SIDs, STARs and approaches may not match in the MCDU. You can join. Check that both pilots use the same AIRAC before you build the flight plan.';
    case 'aircraft.app_version':
    case 'aircraft.package_version': {
      const what = row.field === 'aircraft.app_version' ? 'app' : 'package';
      if (row.result === 'warn')
        return `The aircraft ${what} version could not be read ${whereUnknown(row, other)}, so it cannot be compared. You can join, but make sure you both run the same aircraft build.`;
      return `The aircraft builds are different. Update the aircraft so you both run the same build: the ${other} has ${what} ${row.other}, you have ${row.you}.`;
    }
    case 'bridge_version':
      return `These Crosscheck versions cannot work together. Install the same Crosscheck version as the ${other} (${row.other}).`;
    case 'profile':
      return `The aircraft profiles are different. Install the same Crosscheck version as the ${other}, so you both have the same profile.`;
  }
}

// ---- a control that differs (one line per control, CRO-34) ----

/** "your cockpit" / "the captain's cockpit". */
const cockpit = (where: PanelLine['where'], peer: string) => (where === 'here' ? 'your cockpit' : `the ${peer}'s cockpit`);
/** The cockpit it did not reach: the other one. */
const otherCockpit = (where: PanelLine['where'], peer: string) => (where === 'here' ? `the ${peer}'s cockpit` : 'your cockpit');

/**
 * What the line says, short for the headline and in full for the list. Each one ends with what to do
 * (the templates of gate test 2 section 4).
 */
export function lineText(line: PanelLine, peer: string): { short: string; long: string } {
  const x = line.label;
  switch (line.kind) {
    case 'set_both':
      return {
        short: `You ${line.you?.text ?? 'unknown'}, the ${peer} ${line.other?.text ?? 'unknown'}.`,
        long: 'Set it in the cockpit, or choose one value for both of you here.',
      };
    case 'lamp':
      return {
        short: line.where === 'here' ? 'Lit here only.' : `Lit in the ${peer}'s cockpit only.`,
        long: 'A press in the cockpit is mirrored and would switch both. Press it here only, in the cockpit that is wrong.',
      };
    case 'turn':
      return { short: `Turn ${x} in the cockpit that is wrong.`, long: 'Crosscheck never turns a knob by itself.' };
    case 'release':
      return line.where === 'here'
        ? { short: 'Stuck pressed in your cockpit.', long: 'Nobody is holding it. Release it lifts it once, in your cockpit only.' }
        : { short: `Stuck pressed in the ${peer}'s cockpit.`, long: `The ${peer} can release it there.` };
    case 'not_mirrored':
      return {
        short: `Pressed in ${cockpit(line.where, peer)} only.`,
        long: `Not mirrored (irreversible). Do the same in ${otherCockpit(line.where, peer)} if intended.`,
      };
    case 'by_hand':
      return { short: 'Crosscheck cannot set this yet.', long: 'Set it by hand in both cockpits.' };
    case 'held':
      return {
        short: `Held down in ${cockpit(line.where, peer)} only.`,
        long: 'Crosscheck never presses or releases it by itself.',
      };
    case 'released':
      return {
        short: `Crosscheck released it in ${cockpit(line.where, peer)}.`,
        long: 'The release never arrived there. Check it in both cockpits.',
      };
    case 'press_lost':
      return { short: `A press did not arrive in ${cockpit(line.where, peer)}.`, long: `Press ${x} in the cockpit that is wrong.` };
    case 'mode':
      return { short: 'Managed in one cockpit only.', long: 'Push or pull the knob in the cockpit that is wrong.' };
    case 'cannot_write':
      return {
        short: `Crosscheck cannot write ${x}${line.where === 'there' ? ` in the ${peer}'s cockpit` : line.where === 'here' ? ' in your cockpit' : ''}.`,
        long: 'Set it by hand in both cockpits. Send the log after the flight.',
      };
  }
}

/** The button of a line, or null when the pilots fix it in the cockpit. */
export function actionLabel(a: LineAction, optionText?: string): string {
  switch (a.type) {
    case 'set_both':
      return `Set both to ${optionText ?? ''}`.trim();
    case 'press_here':
      return `Press ${a.label} here only`;
    case 'release':
      return 'Release it';
  }
}

/** "+3 more" next to the headline. */
export const moreText = (n: number) => `+${n} more`;

/** One amber line for owned values (loaded by each pilot, never synced): the largest difference. */
export function driftWarningText(rows: OwnedRow[]): string | null {
  const warn = rows.filter((o) => o.warn).sort((a, b) => Math.abs(b.delta) - Math.abs(a.delta));
  const top = warn[0];
  if (!top) return null;
  const amount = Math.round(Math.abs(top.delta) * 10) / 10;
  const what = /fuel/i.test(top.label) ? 'Fuel' : top.label;
  return `${what} differs by ${amount}${top.unit ? ` ${top.unit}` : ''} – load the same fuel in the EFB.`;
}

// ---- amber lines and indicators ----

const whoMoved = (i: { mine?: boolean; who?: string }) => (i.mine ? 'you' : `the ${i.who}`);

/** "Not synced yet: 4 controls" (moved in the last 2 minutes). */
export function notSyncedLine(n: number): string {
  return `Not synced yet: ${n} ${n === 1 ? 'control' : 'controls'}`;
}

/** "moved by you, 20:07:41" */
export function notSyncedDetail(i: PanelItem): string {
  return `moved by ${whoMoved(i)}${i.at ? `, ${timeOfDay(i.at)}` : ''}`;
}

/** "Outside the aircraft: 6 items differ" (walkaround, never red). */
export function outsideLine(n: number): string {
  return `Outside the aircraft: ${n} ${n === 1 ? 'item differs' : 'items differ'}`;
}

export function amberText(a: AmberItem): string {
  switch (a.kind) {
    case 'hardware_fighting':
      return `Hardware fighting: ${a.label ?? 'a control'} keeps switching back and forth between the two cockpits. Leave it to one pilot.`;
    case 'sim_disconnected':
      return a.mine
        ? 'Your simulator is not connected. Your panels are not compared until it is back.'
        : `The ${a.who}'s simulator is not connected. Panels are not compared until it is back.`;
    case 'counter_suspect':
      return `${a.label ?? 'A control'} looks like a press counter, so it is not synced. Set it by hand in both cockpits.`;
    case 'indicator_differs':
      return a.message ?? `${a.label ?? 'An indicator'} differs between the cockpits.`;
    default:
      return `${a.label ?? 'A control'} needs a look in both cockpits.`;
  }
}

export function indicatorsLine(n: number): string {
  return `Indicators differ (${n})`;
}

/** "you 27.3, captain 27.8, since 19:42:10" */
export function itemValues(i: PanelItem, peer: string): string {
  const parts = [];
  if (i.you && i.other) parts.push(`you ${i.you.text}, ${peer} ${i.other.text}`);
  if (i.since) parts.push(`since ${timeOfDay(i.since)}`);
  return parts.join(', ');
}

export function driftText(delta: number, unit: string | null): string {
  const amount = Math.round(Math.abs(delta) * 10) / 10;
  if (amount === 0) return 'Match';
  return `${amount}${unit ? ` ${unit}` : ''} apart`;
}

/** The start screen when the link to the sim is incomplete (refused SimConnect definitions, CRO-40). */
export function simSetupText(detail: string | null | undefined): string {
  return detail ?? 'Crosscheck could not set up its link to the sim. Do not fly with Crosscheck – send the log.';
}

// ---- control handover (one line: "You have control · 3 differences at handover · Show") ----

/** The middle part of the handover line. */
export function handoverLine(n: number): string {
  if (n === 0) return 'Both cockpits matched at handover';
  return `${n} ${n === 1 ? 'difference' : 'differences'} at handover`;
}

/** Behind Show: how it happened and what differed. */
export function handoverDetail(notice: ControlNotice, peer: string): string {
  const how = notice.kind === 'give' ? `The ${peer} gave you control` : 'You took control';
  const open = [...notice.groups, ...notice.vars];
  const at = notice.at ? ` at ${timeOfDay(notice.at)}` : '';
  return open.length === 0
    ? `${how}${at}.`
    : `${how}${at}. Open differences at the handover: ${open.join(', ')}. Listed for your information; nothing was changed.`;
}
