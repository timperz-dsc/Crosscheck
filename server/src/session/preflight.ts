import type { DiffEntry, Preflight } from '../protocol/types.js';

export interface PreflightResult {
  rejects: DiffEntry[];
  warnings: DiffEntry[];
}

/** What a bridge sends when it could not read a version. */
export const UNKNOWN = 'unknown';

const major = (version: string) => version.split(/[.+-]/)[0] ?? '';

/**
 * Compares a joining client's preflight with the session reference (the creator's).
 * Bridge major version, profile and aircraft build must match. AIRAC only warns, and so does
 * an aircraft version that one side could not read ("unknown"): it cannot be compared.
 */
export function comparePreflight(reference: Preflight, joining: Preflight): PreflightResult {
  const rejects: DiffEntry[] = [];
  const warnings: DiffEntry[] = [];
  const reject = (field: DiffEntry['field'], ref: string, join: string, message: string) =>
    rejects.push({ field, reference: ref, joining: join, severity: 'reject', message });

  if (major(reference.bridge_version) !== major(joining.bridge_version)) {
    reject('bridge_version', reference.bridge_version, joining.bridge_version,
      `Bridge major version differs (${reference.bridge_version} vs ${joining.bridge_version}). Both pilots need the same major version.`);
  }
  if (reference.profile_id !== joining.profile_id) {
    reject('profile_id', reference.profile_id, joining.profile_id, 'Different aircraft profiles.');
  }
  if (reference.profile_hash !== joining.profile_hash) {
    reject('profile_hash', reference.profile_hash, joining.profile_hash, 'Profile files differ. Both pilots need the same profile.');
  }
  const build = (field: 'aircraft.package_version' | 'aircraft.app_version', ref: string, join: string, what: string) => {
    if (ref === UNKNOWN || join === UNKNOWN) {
      warnings.push({ field, reference: ref, joining: join, severity: 'warn',
        message: `Aircraft ${what} version unknown on ${ref === UNKNOWN && join === UNKNOWN ? 'both sides' : 'one side'}: it cannot be compared. Make sure both pilots run the same aircraft build.` });
    } else if (ref !== join) {
      reject(field, ref, join, `Aircraft ${what} version differs. Both pilots need the same aircraft build.`);
    }
  };
  build('aircraft.package_version', reference.aircraft.package_version, joining.aircraft.package_version, 'package');
  build('aircraft.app_version', reference.aircraft.app_version, joining.aircraft.app_version, 'app');

  const refAirac = reference.airac ?? null;
  const joinAirac = joining.airac ?? null;
  if (refAirac === null || joinAirac === null) {
    warnings.push({ field: 'airac', reference: refAirac, joining: joinAirac, severity: 'warn',
      message: 'AIRAC unknown on at least one side: nav data cannot be compared, MCDU pages may differ.' });
  } else if (refAirac !== joinAirac) {
    warnings.push({ field: 'airac', reference: refAirac, joining: joinAirac, severity: 'warn',
      message: `AIRAC differs (${refAirac} vs ${joinAirac}): MCDU pages may legitimately differ.` });
  }
  return { rejects, warnings };
}
