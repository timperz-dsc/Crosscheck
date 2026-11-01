// TypeScript view of protocol v1. The JSON Schema in protocol/schema is the source of truth;
// every inbound message is validated against it before these types are trusted.

export const PROTOCOL_VERSION = 1;

export type Seat = 'host' | 'guest';
export type Role = 'pf' | 'pm';
export type Scalar = number | string | null;

export type AbsoluteClass = 'verified' | 'analog' | 'owned';
export type EventClass = 'delta' | 'pulse';
export type CommandClass = AbsoluteClass | EventClass;
export type SnapshotClass = AbsoluteClass | 'delta' | 'observe';

export const ABSOLUTE_CLASSES: ReadonlySet<string> = new Set(['verified', 'analog', 'owned']);
export const isAbsolute = (cls: string): cls is AbsoluteClass => ABSOLUTE_CLASSES.has(cls);

export interface Envelope<T extends string = string, B = Record<string, unknown>> {
  v: 1;
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
  | 'correction_failed' | 'correction_loop' | 'no_pf';

export interface ActiveDesync {
  var: string;
  group: string | null;
  reason: DesyncReason;
  since: number;
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
}
export interface AckBody {
  id: string;
  ok: boolean;
  observed: Scalar;
  superseded?: boolean;
  from?: string;
  attempt?: number;
}
export interface SnapshotHashBody { group: string; hash: string; applied_seq: number }
export interface SnapshotValue { var: string; class: SnapshotClass; value: Scalar }
export interface SnapshotValuesBody { request_id: string; group: string; values: SnapshotValue[] }
export type ControlConfirm = '@peer_lost';
export type ControlKind = 'take' | 'give';
export interface ControlRequestBody { confirm?: ControlConfirm[] }
export interface ControlAckBody { epoch: number }
export interface StreamBody { frame: number; data: Record<string, number>; from?: string }
export interface PingBody { nonce: number; t: number; rtt_ms: number | null }
export interface PongBody { nonce: number; t: number }

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
  'control_changed', 'peer_status',
]);
