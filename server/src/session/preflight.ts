import type { DiffEntry, Preflight } from '../protocol/types.js';

export interface PreflightResult {
  rejects: DiffEntry[];
  warnings: DiffEntry[];
}

const major = (version: string) => version.split(/[.+-]/)[0] ?? '';

/**
 * Compares a joining client's preflight with the session reference (the creator's).
 * Bridge major version, profile and aircraft build must match; AIRAC only warns.
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
  if (reference.aircraft.package_version !== joining.aircraft.package_version) {
    reject('aircraft.package_version', reference.aircraft.package_version, joining.aircraft.package_version,
      'Aircraft package version differs. Both pilots need the same aircraft build.');
  }
  if (reference.aircraft.app_version !== joining.aircraft.app_version) {
    reject('aircraft.app_version', reference.aircraft.app_version, joining.aircraft.app_version,
      'Aircraft app version differs. Both pilots need the same aircraft build.');
  }

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
