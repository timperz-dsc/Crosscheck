// What GET /api/status returns (bridge/Crosscheck.Bridge.Core/Api/UiStatus.cs).

export type Phase = 'idle' | 'preflight' | 'waiting' | 'session' | 'ended';
export type Side = 'captain' | 'fo';
export type Role = 'pf' | 'pm';

export interface ThisComputer {
  sim: { connected: boolean; name: string };
  aircraft: { name: string; app_version: string | null; package_version: string | null; reading: boolean };
  profile: { id: string; builds: string[]; hash: string };
  server: string;
  /** Online-network clients running here (vPilot, xPilot …): only the PF may be connected (CRO-13). */
  network_clients?: string[];
}

/**
 * The physics stream (CRO-13, bridge Follow/PhysicsSync.cs): the PF streams, the follower is frozen and
 * placed. pf_lost: the PF (or the server) is lost and the follower holds where it is until the pilot's yes.
 * released: the pilot took the aircraft while the server was gone and flies unsynced.
 */
export type PhysicsMode = 'off' | 'streaming' | 'waiting' | 'following' | 'pf_lost' | 'released';

export interface Physics {
  mode: PhysicsMode;
  /** The physics link to the sim is up. */
  link: boolean;
  /** Crosscheck holds this aircraft frozen. */
  frozen: boolean;
  /** The sim reports a freeze on that Crosscheck does not hold (a crashed bridge): offer "Release aircraft". */
  frozen_outside: boolean;
  in_air: boolean;
  pf_lost_cause: 'peer' | 'server' | null;
  /** ok = interpolating, late = predicting ahead, holding = frames too late, waiting = none yet. */
  health: 'ok' | 'late' | 'holding' | 'waiting' | null;
  buffer_ms: number;
  late_ms: number;
  /** About the time a frame takes from the PF (round trip to the server), null until measured. */
  latency_ms: number | null;
  /** No frame for 2 s while following. */
  no_stream: boolean;
  /**
   * Thrust levers whose hardware differs from the PF's lever (match them before taking control). mine: this pilot's
   * hardware position, null if never seen to move; detent: the PF's lever by name ("CL").
   */
  levers: { var: string; pf: number; mine: number | null; detent: string }[];
  /** The follower pilot's hardware levers moved (the PF's position was written back): one amber line, at most every 30 s. */
  levers_moved: boolean;
  /** After a sidestick takeover: levers whose hardware is not where the aircraft's lever was (red until matched). */
  takeover_levers: { var: string; aircraft: number; aircraft_detent: string; mine: number | null; mine_detent: string | null }[];
  /** Placement vars the sim refused (no longer written). */
  refused: string[];
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
  /** Differs by more than the profile's drift_warn right now: one amber line. */
  warn?: boolean;
}

export interface LineValue {
  value: number | null;
  text: string;
}

/**
 * What a red line asks the pilots to do (bridge PanelLines.cs; the sentences are in words.ts):
 * set_both (a switch: Set both to …), lamp (lit in one cockpit: Press X here only), turn (a knob display),
 * release (stuck pressed: Release it), not_mirrored (deny list), by_hand (Crosscheck cannot set it),
 * held (held down in one cockpit), released (Crosscheck released it), press_lost, mode (managed differs),
 * cannot_write (SimConnect refused to write it: the pilots set it by hand, CRO-40).
 */
export type LineKind = 'set_both' | 'lamp' | 'turn' | 'release' | 'not_mirrored' | 'by_hand' | 'held' | 'released' | 'press_lost' | 'mode' | 'cannot_write';

export type LineAction =
  | { type: 'set_both'; var: string; options: { value: number; text: string }[] }
  | { type: 'press_here'; var: string; label: string }
  | { type: 'release'; var: string };

/** One red line: one control (a button with its lamp, a knob with its display) that a pilot can fix. */
export interface PanelLine {
  control: string;
  label: string;
  group: string | null;
  group_label: string | null;
  since: string;
  /** 1 safety, 2 flight-critical, 3 overhead systems, 4 pedestal and radios, 5 lights, cabin and the rest. */
  priority: number;
  kind: LineKind;
  /** Which cockpit it is about: this one or the other pilot's. */
  where: 'here' | 'there' | null;
  you: LineValue | null;
  other: LineValue | null;
  action: LineAction | null;
  message: string | null;
  reason: string;
}

/** One control in a collapsed line (outside, indicators, not synced yet). */
export interface PanelItem {
  control: string;
  label: string;
  you?: LineValue;
  other?: LineValue;
  since?: string;
  mine?: boolean;
  who?: string;
  at?: string;
}

/** An amber line of its own: hardware fighting, a counter suspect, a sim that is not connected. */
export interface AmberItem {
  key: string;
  kind: string;
  label: string | null;
  mine: boolean;
  /** "you", "captain" or "first officer". */
  who: string;
  at: string;
  /** indicator_differs: the profile's own text for the pilots (rudder trim, CRO-40). */
  message?: string | null;
}

export interface LogRow {
  type: string;
  text: string;
  at: string;
}

/** The session panel (CRO-34): one line per control, everything else collapsed. */
export interface Panel {
  /** Priority, then oldest first. */
  red: PanelLine[];
  /** Controls not synced yet that a pilot moved in the last 2 minutes. */
  not_synced: { count: number; items: PanelItem[] };
  /** Walkaround items that differ: one amber line. */
  outside: { count: number; items: PanelItem[] };
  amber: AmberItem[];
  /** Indicators that differ: one grey line. */
  indicators: { count: number; items: PanelItem[] };
  /** Newest first, plain labels. */
  log: LogRow[];
}

export interface ControlNotice {
  at: string | null;
  kind: 'take' | 'give' | null;
  groups: string[];
  vars: string[];
}

/** Self-update (null when this copy was not installed with the installer). */
export interface UpdateStatus {
  state: 'none' | 'checking' | 'ready' | 'error';
  version: string | null;
  /** Set when the check was started because the server refused our version. */
  reason: 'unsupported_version' | null;
}

export interface Status {
  phase: Phase;
  bridge_version: string;
  last_error: string | null;
  update?: UpdateStatus | null;
  /** This bridge's own log file, for sending to the developer after a test. */
  log: { folder: string; file: string } | null;
  this_computer: ThisComputer;
  start_blocked: 'sim_not_connected' | 'sim_setup_error' | null;
  /** Why the link to the sim is incomplete (refused SimConnect definitions, CRO-40); blocks every session. */
  sim_setup_error?: string | null;
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
  /** The session panel (CRO-34). */
  panel?: Panel;
  control_notice?: ControlNotice | null;
  /** The physics stream (CRO-13); also between sessions (a frozen aircraft to release). */
  physics?: Physics | null;
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
  /** Leave; releaseInAir = the pilot confirmed releasing the aircraft in the air. */
  leave(releaseInAir?: boolean): Promise<void>;
  /** Switch the sim's freezes off (a crashed bridge, or holding after the PF was lost). */
  releaseAircraft(): Promise<void>;
  takeControl(confirmPeerLost?: boolean): Promise<void>;
  giveControl(): Promise<void>;
  setBoth(variable: string, value: number): Promise<void>;
  pressHere(variable: string): Promise<void>;
  releaseIt(variable: string): Promise<void>;
  setServer(url: string): Promise<void>;
  applyUpdate(): Promise<void>;
}
