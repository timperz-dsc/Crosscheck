// TypeScript view of protocol v3. The JSON Schema in protocol/schema is the source of truth;
// every inbound message is validated against it before these types are trusted.

export const PROTOCOL_VERSION = 3;

/** Shown to a client that speaks another protocol version (for example the 0.1.0 zip). */
export const OUTDATED_MESSAGE = 'Your Crosscheck is out of date. Download the latest version.';

export type Seat = 'host' | 'guest';
export type Role = 'pf' | 'pm';
export type Scalar = number | string | null;

export type AbsoluteClass = 'verified' | 'analog' | 'owned';
export type EventClass = 'delta' | 'pulse';
export type CommandClass = AbsoluteClass | EventClass;
export type SnapshotClass = AbsoluteClass | 'delta' | 'observe' | 'pulse' | 'toggle' | 'mode';

export const ABSOLUTE_CLASSES: ReadonlySet<string> = new Set(['verified', 'analog', 'owned']);
export const isAbsolute = (cls: string): cls is AbsoluteClass => ABSOLUTE_CLASSES.has(cls);

export interface Envelope<T extends string = string, B = Record<string, unknown>> {
  v: typeof PROTOCOL_VERSION;
  type: T;
  session: string | null;
  seq?: number;
  ts: number;
  replay?: boolean;
  body: B;
}

export interface Preflight {
  bridge_version: string;
  profile_id: string;
  profile_hash: string;
  aircraft: { package_version: string; app_version: string };
  airac?: string | null;
}

export type PreflightField =
  | 'bridge_version' | 'profile_id' | 'profile_hash'
  | 'aircraft.package_version' | 'aircraft.app_version' | 'airac';

export interface DiffEntry {
  field: PreflightField;
  reference: string | null;
  joining: string | null;
  severity: 'reject' | 'warn';
  message: string;
}

export type DesyncReason =
  | 'no_ack' | 'refused' | 'observe_mismatch' | 'log_conflict' | 'missing_value'
  | 'correction_failed' | 'correction_loop' | 'no_pf' | 'effect_mismatch'
  | 'safety_release' | 'press_mismatch' | 'toggle_mismatch' | 'mode_mismatch';

/** Where a desync is shown: red (an input did not arrive, sticky, at the top) or quiet (an indicator differs). */
export type Tier = 'red' | 'quiet';

export interface ActiveDesync {
  var: string;
  group: string | null;
  reason: DesyncReason;
  since: number;
  tier: Tier;
}

export type AdvisoryKind = 'not_synced' | 'hardware_fighting' | 'sim_disconnected';

/** An amber line for both pilots (never a desync, never written). The latest body per key wins. */
export interface AdvisoryBody {
  key: string;
  kind: AdvisoryKind;
  active: boolean;
  tier: 'amber';
  var?: string;
  label?: string;
  seat: Seat;
  at: number;
  count?: number;
  message: string;
}

export interface Timing {
  heartbeat_ms: number;
  lost_after_ms: number;
  session_ttl_ms: number;
  snapshot_interval_ms: number;
}

export interface PeerInfo {
  client_id: string;
  seat: Seat;
  role: Role;
  state: 'connected' | 'lost';
  name?: string;
}

export interface Origin {
  client_id: string;
  seat: Seat;
  role: Role;
}

// ---- client → server bodies ----
export interface CreateBody { preflight: Preflight; name?: string }
export interface JoinBody { join_code: string; preflight: Preflight; name?: string }
export interface ResumeBody { client_id: string; resume_token: string; last_seq: number }
export interface CommandBody {
  id: string;
  var: string;
  class: CommandClass;
  value?: number;
  delta?: number;
  group: string;
  settle_ms: number;
  origin?: Origin;
  attempt?: number;
  /** Pulse only: compare this effect var between the sims after settle. */
  effect?: EffectSpec;
  /** Edge push buttons (press: edge): this command is one down or up edge. */
  edge?: 'down' | 'up';
  /** With edge: the sender's time since the previous edge of this button. */
  gap_ms?: number;
}
export interface EffectSpec { var: string; expect: 'same' | 'parity' }
export interface AckBody {
  id: string;
  ok: boolean;
  observed: Scalar;
  superseded?: boolean;
  from?: string;
  attempt?: number;
  /** The receiver's effect var value after settle (commands with effect). */
  effect?: Scalar;
}
export interface SnapshotHashBody { group: string; hash: string; applied_seq: number }
/** mode: an indicator of an input Crosscheck cannot sync (yet), compared as an input (red, never quiet). */
export interface SnapshotValue { var: string; class: SnapshotClass; value: Scalar; step?: number; button?: string; message?: string; report_as?: string }
/** A receiver released an edge button Crosscheck had pressed down, because the up never arrived. */
export interface SafetyReleaseBody { var: string; group: string; cause: 'cap' | 'link_lost' | 'peer_lost'; observed: Scalar }
export interface SnapshotValuesBody { request_id: string; group: string; values: SnapshotValue[] }
export type ControlConfirm = '@peer_lost';
export type ControlKind = 'take' | 'give';
export interface ControlRequestBody { confirm?: ControlConfirm[] }
export interface ControlAckBody { epoch: number }
export interface StreamBody { frame: number; data: Record<string, number>; from?: string }
export interface PingBody { nonce: number; t: number; rtt_ms: number | null }
export interface PongBody { nonce: number; t: number }
export interface SimStateBody { connected: boolean }
export interface UnsyncedInputBody { var: string; label: string }
export interface EffectReportBody { id: string; var: string; value: Scalar }

// ---- server → client bodies (only the ones built in several places) ----
export type DecisionAction = 'correct' | 'desync' | 'held' | 'deferred';
export interface DiffDecision {
  var: string;
  class: SnapshotClass;
  pf: Scalar;
  pm: Scalar;
  action: DecisionAction;
  target?: Scalar;
  sides?: Role[];
  source?: 'command_log' | 'pf_reference';
  source_seq?: number;
  reason?: DesyncReason;
}

export interface CorrectionBody {
  id: string;
  var: string;
  class: AbsoluteClass | 'delta';
  target: number;
  group: string;
  settle_ms: number;
  source: 'command_log' | 'pf_reference';
  source_seq?: number;
  request_id: string;
  attempt?: number;
}

export type ErrorCode =
  | 'invalid_json' | 'invalid_message' | 'unsupported_version' | 'not_in_session'
  | 'bad_command' | 'resume_failed' | 'forbidden';

export type NoticeKind = 'overridden' | 'corrected' | 'desync_cleared' | 'stream_dropped' | 'control_ack_missing';

/** Server-to-client messages that are kept in the replay log and re-sent after a resume. */
export const REPLAYABLE: ReadonlySet<string> = new Set([
  'command', 'accepted', 'ack', 'desync', 'notice', 'correction', 'snapshot_diff',
  'control_changed', 'peer_status', 'advisory',
]);
