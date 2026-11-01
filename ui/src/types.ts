// What GET /api/status returns (bridge/Crosscheck.Bridge.Core/Api/UiStatus.cs).

export type Phase = 'idle' | 'preflight' | 'waiting' | 'session' | 'ended';
export type Side = 'captain' | 'fo';
export type Role = 'pf' | 'pm';

export interface ThisComputer {
  sim: { connected: boolean; name: string };
  aircraft: { name: string; app_version: string | null; package_version: string | null; reading: boolean };
  profile: { id: string; builds: string[]; hash: string };
  server: string;
}

export interface PreflightRow {
  field: 'bridge_version' | 'profile' | 'aircraft.package_version' | 'aircraft.app_version' | 'airac';
  item: string;
  you: string;
  other: string;
  result: 'same' | 'warn' | 'reject';
}

export interface Preflight {
  code: string;
  joinable: boolean;
  rows: PreflightRow[];
}

export interface CrewMember {
  side: Side;
  you: boolean;
  role: Role;
  state: string;
  name?: string | null;
}

export interface GroupRow {
  id: string;
  label: string;
  differs: number;
}

export interface OwnedRow {
  var: string;
  label: string;
  unit: string | null;
  delta: number;
  /** Differed by more than the profile's drift_warn when the session started, and still does. */
  warn?: boolean;
}

export interface DesyncValue {
  value: number | null;
  text: string;
}

export interface Desync {
  var: string;
  label: string;
  has_label: boolean;
  group: string | null;
  group_label: string | null;
  reason: string;
  since: string;
  receiver_is_you: boolean;
  you: DesyncValue;
  other: DesyncValue;
  writable: boolean;
  options: { value: number; text: string }[];
  message: string | null;
}

export interface ControlNotice {
  at: string | null;
  kind: 'take' | 'give' | null;
  groups: string[];
  vars: string[];
}

export interface Status {
  phase: Phase;
  bridge_version: string;
  last_error: string | null;
  /** This bridge's own log file, for sending to the developer after a test. */
  log: { folder: string; file: string } | null;
  this_computer: ThisComputer;
  start_blocked: 'sim_not_connected' | null;
  preflight: Preflight | null;
  join_code: string | null;
  seat: string | null;
  seat_side: Side | null;
  role: Role | null;
  server: string;
  rtt_ms: number | null;
  peer: string | null;
  is_pf?: boolean;
  crew?: CrewMember[];
  peer_lost_at?: string | null;
  session_ttl_ms?: number;
  last_compared_at?: string | null;
  groups?: GroupRow[];
  owned?: OwnedRow[];
  desyncs?: Desync[];
  control_notice?: ControlNotice | null;
}

export interface RecentItem {
  type: string;
  message: string;
  at: string;
}

/** Event from WS /api/events. */
export interface BridgeEvent {
  type: string;
  message: string;
  at: string;
  data: Record<string, unknown> | null;
}

/** What the screens can ask the bridge to do. */
export interface Actions {
  create(): Promise<void>;
  check(code: string): Promise<void>;
  join(code: string): Promise<void>;
  cancel(): Promise<void>;
  leave(): Promise<void>;
  takeControl(confirmPeerLost?: boolean): Promise<void>;
  giveControl(): Promise<void>;
  setBoth(variable: string, value: number): Promise<void>;
  setServer(url: string): Promise<void>;
}
