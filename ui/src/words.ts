// The sentences the UI shows. Plain, specific, sentence case.
import type { Advisory, CrewMember, Desync, OwnedRow, PreflightRow, Side, Status } from './types';

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
  if (n === 0) return 'Both cockpits match';
  const count = NUMBERS[n] ?? String(n);
  return n === 1 ? `${count} control differs` : `${count} controls differ`;
}

export function serverText(s: Status): string {
  if (s.server === 'connected') return s.rtt_ms != null ? `Connected, ${Math.round(s.rtt_ms)} ms` : 'Connected';
  if (s.server === 'reconnecting') return 'Reconnecting';
  return 'Not connected';
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

// ---- a control that differs ----

export function desyncExplanation(d: Desync, peer: string): string {
  const what = (() => {
    switch (d.reason) {
      case 'no_ack':
        return d.receiver_is_you
          ? `The ${peer} changed it, but your sim did not confirm it.`
          : `You changed it, but the ${peer}'s sim did not confirm it.`;
      case 'refused':
        return d.receiver_is_you
          ? `The ${peer} changed it, but it did not change in your cockpit.`
          : `You changed it, but it did not change in the ${peer}'s cockpit.`;
      case 'effect_mismatch':
        return d.receiver_is_you
          ? `The ${peer} pressed it. The press arrived, but its light shows a different result in your cockpit.`
          : `You pressed it. The press arrived, but its light shows a different result in the ${peer}'s cockpit.`;
      case 'correction_failed':
        return 'It was set to match the pilot flying, but the change did not take effect.';
      case 'correction_loop':
        return 'It was set back more than 3 times within a minute and keeps changing, so it is no longer set automatically.';
      case 'observe_mismatch':
        return 'This is an indicator, so it cannot be set. It shows a different state in the two cockpits; check the switches that drive it.';
      case 'log_conflict':
        return 'Neither cockpit shows the value of the last pilot command, so it was not changed automatically.';
      case 'missing_value':
        return 'One of the sims does not report this value.';
      case 'no_pf':
        return 'There is no pilot flying, so it was not changed automatically.';
      default:
        return d.message ?? 'The two cockpits show different values.';
    }
  })();
  return d.writable && d.options.length > 0
    ? `${what} Set it in the cockpit, or choose a value for both of you here. The alarm clears when both match.`
    : `${what} The alarm clears when both match.`;
}

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

const who = (a: Advisory) => (a.mine ? 'You' : `The ${a.who}`);

/** "You moved ENG MODE – not synced yet." */
export function notSyncedText(a: Advisory): string {
  return `${who(a)} moved ${a.label ?? a.var ?? 'a control'} – not synced yet.`;
}

export function advisoryText(a: Advisory): string {
  switch (a.kind) {
    case 'not_synced':
      return notSyncedText(a);
    case 'hardware_fighting':
      return `${a.label ?? a.var} keeps switching back and forth between the two cockpits. Both hardware controls may be setting it; leave it to one pilot.`;
    case 'sim_disconnected':
      return a.mine
        ? 'Your simulator is not connected. Your panels are not compared until it is back.'
        : `The ${a.who}'s simulator is not connected. Panels are not compared until it is back.`;
    default:
      return a.message ?? '';
  }
}

export function indicatorsLine(n: number): string {
  return `Indicators differ (${n})`;
}

export function effectsText(effects: Desync[]): string {
  return `Likely effects: ${effects.map((e) => e.label).join(', ')}.`;
}

export function driftText(delta: number, unit: string | null): string {
  const amount = Math.round(Math.abs(delta) * 10) / 10;
  if (amount === 0) return 'Match';
  return `${amount}${unit ? ` ${unit}` : ''} apart`;
}
