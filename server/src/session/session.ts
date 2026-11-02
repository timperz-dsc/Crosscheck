import { timingSafeEqual } from 'node:crypto';
import type { ServerConfig } from '../config.js';
import { SessionLog } from '../log/jsonl.js';
import type { ProtocolValidator } from '../protocol/validate.js';
import {
  PROTOCOL_VERSION, REPLAYABLE, isAbsolute,
  type AckBody, type ActiveDesync, type AdvisoryBody, type CommandBody, type CommandClass, type ControlAckBody,
  type ControlConfirm, type ControlKind, type ControlRequestBody, type CorrectionBody, type DesyncReason, type DiffDecision, type DiffEntry,
  type EffectReportBody, type EffectSpec, type Envelope, type PeerInfo, type PingBody, type PongBody, type Preflight, type Role, type Scalar,
  type Seat, type SimStateBody, type SnapshotHashBody, type SnapshotValue, type SnapshotValuesBody, type StreamBody, type Tier, type Timing,
  type SafetyReleaseBody, type UnsyncedInputBody,
} from '../protocol/types.js';
import { CorrectionGuard } from './correctionGuard.js';
import { buttonDecision, decideCorrections, diffValues, type LoggedCommand, type RawDecision } from './corrections.js';
import { newClientId, newResumeToken } from './joinCode.js';
import { PendingAcks, type PendingEntry } from './pendingAcks.js';
import { comparePreflight } from './preflight.js';
import { ReplayLog } from './replayLog.js';

/** The transport side of one client socket. */
export interface Conn {
  send(text: string): void;
  terminate(): void;
  isOpen(): boolean;
  slot?: ClientSlot;
  session?: Session;
}

interface StreamStats {
  frames: number;
  dropped: number;
  active: boolean;
  lastAt: number;
  windowStart: number;
  windowCount: number;
  lastNoticeAt: number;
}

export interface ClientSlot {
  id: string;
  seat: Seat;
  token: string;
  name?: string;
  preflight: Preflight;
  conn: Conn | null;
  state: 'connected' | 'lost';
  lastRxAt: number;
  lastPongAt: number;
  rttMs: number | null;
  clientRttMs: number | null;
  pingNonce: number;
  expiryTimer: NodeJS.Timeout | null;
  stream: StreamStats;
  /** The bridge's simulator is connected (sim_state). While false its groups are not comparable. */
  simConnected: boolean;
}

interface CommandRecord {
  seq: number;
  id: string;
  origin: string;
  var: string;
  cls: CommandClass;
  value?: number;
  delta?: number;
  group: string;
  receivedAt: number;
  /** Clients that acked this command ok (not superseded). */
  okBy: Set<string>;
}

interface GroupState {
  reports: Map<string, { hash: string; appliedSeq: number; at: number }>;
  mismatchCount: number;
  inFlight: string | null;
  /** Last time a command/correction in this group started or finished. */
  quietSince: number;
  /** A decision fell into a control handover freeze; re-evaluate afterwards. */
  deferred: boolean;
  /** Vars that differed in the last snapshot diff of this group (owned drift excluded); cleared when the hashes match. */
  diffVars: string[];
  /** Owned vars that drifted in the last comparison; a drift message with no values clears them. */
  driftVars: string[];
  /** Observe vars that differ, since when (raised only after config.indicatorHoldMs). */
  observeSince: Map<string, number>;
  /** Edge buttons (pulse parity, toggle lamp) that differ: since when and in how many comparisons in a row. */
  buttonSince: Map<string, { at: number; rounds: number }>;
  /** Owned drift as last sent to the pilots (rate limit), and when. */
  driftSent: Map<string, number>;
  driftSentAt: number;
}

/** A pulse with an effect check: the effect values of both sims after settle are compared. */
interface EffectCheck {
  id: string;
  var: string;
  group: string;
  effect: EffectSpec;
  origin: string;
  receiver: string;
  sender?: Scalar;
  received?: Scalar;
  timer: NodeJS.Timeout;
}

type RequestReason = 'mismatch' | 'resync' | 'join' | 'control';

interface SnapshotRequestState {
  id: string;
  reason: RequestReason;
  groups: string[] | null;
  responses: Map<string, Map<string, SnapshotValue[]>>;
  done: Set<string>;
  timer: NodeJS.Timeout;
}

interface OpenDesync extends ActiveDesync {
  /** Blocking desyncs stop automatic corrections for the var until they clear. */
  blocking: boolean;
  /**
   * The snapshot var whose match clears this desync (usually the var itself). null: never cleared
   * by a snapshot (pulses are not in snapshots; only a confirmed press clears them).
   */
  matchVar: string | null;
}

type JoinResult =
  | { ok: true; slot: ClientSlot }
  | { ok: false; reason: 'session_full' | 'preflight_mismatch'; message: string; diff: DiffEntry[] };

const MAX_REMEMBERED_IDS = 10000;
/** Failed command ids kept so that a late ok ack can still clear the desync. */
const MAX_FAILED_IDS = 500;
const NOT_LOGGED = new Set(['stream', 'ping', 'pong']);

export class Session {
  readonly slots: ClientSlot[] = [];
  pfClientId: string | null = null;
  readonly log: SessionLog;

  private seq = 0;
  private readonly replay: ReplayLog;
  private readonly pending: PendingAcks;
  private readonly guard: CorrectionGuard;
  private readonly commands = new Map<string, CommandRecord>();
  private readonly lastCommandByVar = new Map<string, CommandRecord>();
  private readonly groups = new Map<string, GroupState>();
  private readonly requests = new Map<string, SnapshotRequestState>();
  private readonly desyncs = new Map<string, OpenDesync>();
  /** Amber lines by key; only active ones are kept. */
  private readonly advisories = new Map<string, AdvisoryBody>();
  /** Failed command id → var and origin (a late ok ack clears the desync and reaches the origin). */
  private readonly failedIds = new Map<string, { var: string; origin?: string; attempt: number }>();
  /** Recent overrides per var (hardware fighting) and the timer that ends the advisory. */
  private readonly overrides = new Map<string, { times: number[]; timer: NodeJS.Timeout | null }>();
  private readonly effectChecks = new Map<string, EffectCheck>();
  /** Drift step per owned var, from snapshot values. */
  private readonly driftSteps = new Map<string, number>();
  private handover: { epoch: number; kind: ControlKind; newPf: string; prevPf: string | null; confirmed: ControlConfirm[]; timer: NodeJS.Timeout } | null = null;
  /** Last stream frame relayed from the PF; handed to the new PF in control_changed. */
  private lastFrame: { frame: number; data: Record<string, number>; from: string } | null = null;
  private controlAcks: { epoch: number; acked: Set<string>; timer: NodeJS.Timeout } | null = null;
  private epoch = 1;
  private requestCounter = 0;
  private readonly tickTimer: NodeJS.Timeout;
  private readonly summaryTimer: NodeJS.Timeout;
  private ended = false;

  constructor(
    readonly id: string,
    readonly joinCode: string,
    readonly reference: Preflight,
    private readonly config: ServerConfig,
    private readonly validator: ProtocolValidator,
    private readonly onEnd: (session: Session) => void,
  ) {
    this.log = new SessionLog(config.logDir, id, config.echoEvents);
    this.replay = new ReplayLog(config.logSize);
    this.guard = new CorrectionGuard(config.correctionLoopMax, config.correctionLoopWindowMs);
    this.pending = new PendingAcks({
      maxRetries: config.maxRetries,
      timeoutFor: (e) => e.settleMs + config.ackMarginMs + (this.slot(e.to)?.rttMs ?? 0),
      resend: (e) => this.resend(e),
      ok: (e, ack) => this.pendingOk(e, ack),
      failed: (e, reason) => this.pendingFailed(e, reason),
      changed: (e) => { this.group(e.group).quietSince = Date.now(); },
    });
    this.tickTimer = setInterval(() => this.tick(), config.heartbeatMs);
    this.summaryTimer = setInterval(() => this.summary(), config.summaryIntervalMs);
    this.log.event('session_created', { session: id, join_code: joinCode, reference });
  }

  // ------------------------------------------------------------------ membership

  /** The creator: host seat, pilot flying. */
  addHost(conn: Conn, preflight: Preflight, name: string | undefined): ClientSlot {
    const slot = this.addSlot(conn, 'host', preflight, name);
    this.pfClientId = slot.id;
    this.emit([slot], 'created', {
      session_id: this.id, join_code: this.joinCode, client_id: slot.id, resume_token: slot.token,
      seat: slot.seat, role: 'pf', pf_client_id: slot.id, config: this.timing(),
    });
    return slot;
  }

  join(conn: Conn, preflight: Preflight, name: string | undefined): JoinResult {
    if (this.slots.length >= 2) {
      this.log.event('join_rejected', { reason: 'session_full' });
      return { ok: false, reason: 'session_full', message: 'This session already has two pilots.', diff: [] };
    }
    const { rejects, warnings } = comparePreflight(this.reference, preflight);
    if (rejects.length > 0) {
      const diff = [...rejects, ...warnings];
      this.log.event('join_rejected', { reason: 'preflight_mismatch', diff, preflight });
      return { ok: false, reason: 'preflight_mismatch', message: `Preflight mismatch: ${rejects.map((r) => r.field).join(', ')}.`, diff };
    }
    const seat: Seat = this.slots.some((s) => s.seat === 'host') ? 'guest' : 'host';
    const slot = this.addSlot(conn, seat, preflight, name);
    const peer = this.other(slot);
    this.emit([slot], 'joined', {
      session_id: this.id, client_id: slot.id, resume_token: slot.token, seat, role: this.role(slot),
      pf_client_id: this.pfClientId, peer: peer ? this.peerInfo(peer) : null, warnings, config: this.timing(),
      advisories: this.openAdvisories(),
    });
    if (peer) {
      this.emit([peer], 'peer_status', {
        client_id: slot.id, seat, role: this.role(slot), state: 'joined', ...(name ? { name } : {}), warnings,
      });
    }
    // A fresh join aligns through the same path as a resync: compare everything, correct per PF rule.
    this.requestSnapshot(null, 'join');
    return { ok: true, slot };
  }

  checkToken(slot: ClientSlot, token: string): boolean {
    const a = Buffer.from(slot.token);
    const b = Buffer.from(token);
    return a.length === b.length && timingSafeEqual(a, b);
  }

  resume(slot: ClientSlot, conn: Conn, lastSeq: number): void {
    const old = slot.conn;
    if (old && old !== conn) {
      old.slot = undefined;
      old.session = undefined;
      old.terminate();
    }
    if (slot.expiryTimer) clearTimeout(slot.expiryTimer);
    slot.expiryTimer = null;
    this.attach(slot, conn);

    const replay = this.replay.replayFor(slot.id, lastSeq);
    const fullResync = replay === null;
    const peer = this.other(slot);
    this.log.event('client_resumed', { client: slot.id, last_seq: lastSeq, replayed: replay?.length ?? 0, full_resync: fullResync });
    this.sendUnsequenced(slot, 'resumed', {
      session_id: this.id, client_id: slot.id, seat: slot.seat, role: this.role(slot), pf_client_id: this.pfClientId,
      replayed: replay?.length ?? 0, full_resync: fullResync, desyncs: this.openDesyncs(),
      peer: peer ? this.peerInfo(peer) : null, config: this.timing(), advisories: this.openAdvisories(),
    });
    if (replay) {
      for (const msg of replay) this.sendRaw(slot, { ...msg, replay: true });
      this.pending.resumeFor(slot.id, lastSeq);
    } else {
      const dropped = this.pending.cancelWhere((e) => e.to === slot.id);
      if (dropped.length > 0) this.log.event('pending_dropped_for_resync', { client: slot.id, ids: dropped.map((e) => e.id) });
    }
    if (peer) this.emit([peer], 'peer_status', { client_id: slot.id, seat: slot.seat, role: this.role(slot), state: 'resumed' });
    if (fullResync) this.requestSnapshot(null, 'resync');
  }

  /** Socket closed under a connected client. */
  connectionClosed(slot: ClientSlot, conn: Conn): void {
    if (!this.ended && slot.conn === conn) this.markLost(slot, 'closed');
  }

  slot(id: string): ClientSlot | undefined {
    return this.slots.find((s) => s.id === id);
  }

  // ------------------------------------------------------------------ inbound

  handle(slot: ClientSlot, msg: Envelope): void {
    if (this.ended) return;
    slot.lastRxAt = Date.now();
    if (!NOT_LOGGED.has(msg.type)) this.log.write({ dir: 'in', client: slot.id, msg });
    const body = msg.body as never;
    switch (msg.type) {
      case 'command': return this.onCommand(slot, body);
      case 'ack': return this.onAck(slot, body);
      case 'snapshot_hash': return this.onSnapshotHash(slot, body);
      case 'snapshot_values': return this.onSnapshotValues(slot, body);
      case 'control_request': return this.onControlRequest(slot, body);
      case 'control_give': return this.onControlGive(slot);
      case 'control_ack': return this.onControlAck(slot, body);
      case 'stream': return this.onStream(slot, body);
      case 'ping': return this.onPing(slot, body);
      case 'pong': return this.onPong(slot, body);
      case 'leave': return this.removeSlot(slot, 'left');
      case 'sim_state': return this.onSimState(slot, body);
      case 'unsynced_input': return this.onUnsyncedInput(slot, body);
      case 'effect_report': return this.onEffectReport(slot, body);
      case 'safety_release': return this.onSafetyRelease(slot, body);
      default:
        this.sendError(slot, 'forbidden', `${msg.type} is not allowed from a client in a session`);
    }
  }

  // ------------------------------------------------------------------ commands

  private onCommand(slot: ClientSlot, body: CommandBody): void {
    const now = Date.now();
    const known = this.commands.get(body.id);
    if (known) {
      if (known.origin !== slot.id) return this.sendError(slot, 'bad_command', `command id ${body.id} is already used by the other pilot`);
      // A re-send after reconnect: already logged, never relay twice.
      this.emit([slot], 'accepted', { id: body.id, duplicate: true }, { seq: known.seq, replayable: false });
      return;
    }
    const absolute = isAbsolute(body.class);
    const peer = this.other(slot);

    // Seq order: a newer absolute command makes older retries on the same var stale.
    if (absolute) {
      const stale = this.pending.cancelWhere((e) => e.var === body.var && isAbsolute(e.cls));
      if (stale.length > 0) this.log.event('pending_superseded', { var: body.var, ids: stale.map((e) => e.id) });
    }
    const prev = this.lastCommandByVar.get(body.var);
    const conflict = absolute && prev && isAbsolute(prev.cls) && prev.origin !== slot.id && now - prev.receivedAt <= this.config.conflictWindowMs
      ? prev : undefined;

    // An open desync for this var stays open until this command is acked ok (or the values
    // match): a new click that is refused too must never wipe the red line (CRO-26).

    const relay: CommandBody = {
      id: body.id, var: body.var, class: body.class,
      ...(body.value !== undefined ? { value: body.value } : {}),
      ...(body.delta !== undefined ? { delta: body.delta } : {}),
      group: body.group, settle_ms: body.settle_ms,
      ...(body.effect ? { effect: body.effect } : {}),
      ...(body.edge ? { edge: body.edge, gap_ms: body.gap_ms ?? 0 } : {}),
      origin: { client_id: slot.id, seat: slot.seat, role: this.role(slot) }, attempt: 0,
    };
    let seq: number;
    let relayed: Envelope | null = null;
    if (peer) {
      const sent = this.emit([peer], 'command', relay);
      seq = sent.seq;
      relayed = sent.env;
    } else {
      seq = ++this.seq;
    }
    this.emit([slot], 'accepted', { id: body.id }, { seq });

    const rec: CommandRecord = {
      seq, id: body.id, origin: slot.id, var: body.var, cls: body.class, group: body.group, receivedAt: now, okBy: new Set(),
      ...(body.value !== undefined ? { value: body.value } : {}),
      ...(body.delta !== undefined ? { delta: body.delta } : {}),
    };
    this.remember(rec);
    this.lastCommandByVar.set(body.var, rec);
    this.group(body.group).quietSince = now;

    if (peer && relayed) {
      this.pending.start({
        kind: 'command', id: body.id, seq, to: peer.id, origin: slot.id, var: body.var, group: body.group, cls: body.class,
        expected: body.value ?? null, ...(body.delta !== undefined ? { delta: body.delta } : {}),
        settleMs: body.settle_ms, message: relayed, retry: absolute,
      }, peer.state === 'connected');
      if (body.effect && body.class === 'pulse') this.startEffectCheck(body, slot, peer);
    }

    if (conflict) {
      const earlier = this.slot(conflict.origin);
      if (earlier) {
        const role = this.role(slot);
        this.emit([earlier], 'notice', {
          kind: 'overridden', id: conflict.id, var: body.var, by_client_id: slot.id, by_seat: slot.seat, by_role: role, by_seq: seq,
          message: `${body.var} overridden by ${role.toUpperCase()} (${slot.seat}).`,
        });
      }
      this.recordOverride(body.var, slot);
    }
  }

  private remember(rec: CommandRecord): void {
    this.commands.set(rec.id, rec);
    if (this.commands.size > MAX_REMEMBERED_IDS) {
      const oldest = this.commands.keys().next().value;
      if (oldest !== undefined) this.commands.delete(oldest);
    }
  }

  private onAck(slot: ClientSlot, body: AckBody): void {
    if (this.pending.ack(slot.id, body)) return;
    // A late ok for a command that already failed: the input did arrive after all.
    const failed = body.ok ? this.failedIds.get(body.id) : undefined;
    if (failed !== undefined) {
      this.failedIds.delete(body.id);
      this.log.event('late_ack_ok', { client: slot.id, id: body.id, var: failed.var });
      const origin = failed.origin ? this.slot(failed.origin) : undefined;
      if (origin) this.emit([origin], 'ack', { id: body.id, ok: true, observed: body.observed, from: slot.id, attempt: failed.attempt });
      this.clearDesync(failed.var, 'confirmed');
      return;
    }
    this.log.event('ack_unmatched', { client: slot.id, id: body.id });
  }

  private resend(e: PendingEntry): void {
    const target = this.slot(e.to);
    if (!target) return;
    const msg: Envelope = { ...e.message, ts: Date.now(), body: { ...e.message.body, attempt: e.attempt } };
    this.sendRaw(target, msg);
    this.log.write({ dir: 'out', to: [target.id], msg });
  }

  private pendingOk(e: PendingEntry, ack: AckBody): void {
    if (e.kind === 'command') {
      const rec = this.commands.get(e.id);
      if (rec && !ack.superseded) rec.okBy.add(e.to);
      const origin = e.origin ? this.slot(e.origin) : undefined;
      if (origin) {
        this.emit([origin], 'ack', {
          id: e.id, ok: true, observed: ack.observed, ...(ack.superseded ? { superseded: true } : {}), from: e.to, attempt: e.attempt,
          ...(ack.effect !== undefined ? { effect: ack.effect } : {}),
        });
      }
      // The input arrived: an open desync for this var is resolved by it.
      this.clearDesync(e.var, 'confirmed');
      const check = this.effectChecks.get(e.id);
      if (check) {
        check.received = ack.effect ?? null;
        this.completeEffectCheck(check, false);
      }
    } else {
      const target = this.slot(e.to);
      this.emit(this.slots, 'notice', {
        kind: 'corrected', var: e.var, group: e.group, target: e.expected, client_id: e.to, ...(e.side ? { side: e.side } : {}),
        message: `${e.var} corrected to ${String(e.expected)} on the ${e.side?.toUpperCase() ?? ''} side (${target?.seat ?? e.to}).`,
      });
      this.clearDesync(e.var, 'corrected');
    }
  }

  private pendingFailed(e: PendingEntry, reason: 'no_ack' | 'refused'): void {
    const receiver = this.slot(e.to);
    const observed = e.lastAck?.observed ?? null;
    if (e.kind === 'command') {
      const origin = e.origin ? this.slot(e.origin) : undefined;
      if (origin && e.lastAck) {
        this.emit([origin], 'ack', { id: e.id, ok: false, observed, from: e.to, attempt: e.attempt });
      }
      const tries = e.retry ? ` after ${this.config.maxRetries} re-sends` : '';
      const what = reason === 'no_ack' ? `no ack${tries}` : `value refused${tries}`;
      this.failedIds.set(e.id, { var: e.var, ...(e.origin ? { origin: e.origin } : {}), attempt: e.attempt });
      if (this.failedIds.size > MAX_FAILED_IDS) this.failedIds.delete(this.failedIds.keys().next().value!);
      this.dropEffectCheck(e.id);
      this.raiseDesync({
        var: e.var, group: e.group, reason, expected: e.expected, observed,
        ...(e.delta !== undefined ? { delta: e.delta } : {}), command_id: e.id, client_id: e.to,
        message: `${e.var}: ${what} on the ${receiver?.seat ?? 'other'} side.`,
      }, false, 'red', e.cls === 'pulse' ? null : e.var);
    } else {
      this.raiseDesync({
        var: e.var, group: e.group, reason: 'correction_failed', expected: e.expected, observed, command_id: e.id, client_id: e.to,
        message: `${e.var}: correction to ${String(e.expected)} did not take effect on the ${receiver?.seat ?? 'other'} side.`,
      }, true, 'red');
    }
  }

  // ------------------------------------------------------------------ desyncs

  /**
   * Opens a desync for both pilots. tier red: a pilot input did not arrive (or its result differs);
   * quiet: an indicator differs. Only observe vars may ever be quiet (see tierFor).
   */
  private raiseDesync(
    body: { var: string; group?: string; reason: DesyncReason; expected: Scalar; observed: Scalar; message: string } & Record<string, unknown>,
    blocking: boolean, tier: Tier, matchVar: string | null = body.var,
  ): void {
    const open = this.desyncs.get(body.var);
    if (open && open.reason === body.reason) {
      open.blocking ||= blocking;
      this.log.event('desync_still_open', { var: body.var, reason: body.reason });
      return;
    }
    this.desyncs.set(body.var, { var: body.var, group: body.group ?? null, reason: body.reason, since: Date.now(), tier, blocking, matchVar });
    this.emit(this.slots, 'desync', { ...body, tier });
  }

  /** A desync clears only when the values match, a command for the var is confirmed, or a correction is confirmed. */
  private clearDesync(name: string, why: 'match' | 'confirmed' | 'corrected'): void {
    const open = this.desyncs.get(name);
    if (!open) return;
    this.desyncs.delete(name);
    this.guard.reset(name);
    const text = { match: 'values match again', confirmed: 'a command for it was confirmed', corrected: 'correction confirmed' }[why];
    this.emit(this.slots, 'notice', {
      kind: 'desync_cleared', var: name, ...(open.group ? { group: open.group } : {}), reason: open.reason,
      message: `${name}: desync cleared (${text}).`,
    });
  }

  private openDesyncs(): ActiveDesync[] {
    return [...this.desyncs.values()].map(({ blocking: _blocking, matchVar: _matchVar, ...d }) => d);
  }

  // ------------------------------------------------------------------ snapshots

  private group(name: string): GroupState {
    let g = this.groups.get(name);
    if (!g) {
      g = {
        reports: new Map(), mismatchCount: 0, inFlight: null, quietSince: 0, deferred: false, diffVars: [], driftVars: [],
        observeSince: new Map(), buttonSince: new Map(), driftSent: new Map(), driftSentAt: 0,
      };
      this.groups.set(name, g);
    }
    return g;
  }

  private onSnapshotHash(slot: ClientSlot, body: SnapshotHashBody): void {
    const g = this.group(body.group);
    g.reports.set(slot.id, { hash: body.hash, appliedSeq: body.applied_seq, at: Date.now() });
    this.compareGroup(body.group, g);
  }

  /**
   * Compares the latest hashes of both pilots for one group. Only when nothing is in flight
   * in that group and both hashes were taken after the last command/correction settled.
   * A mismatch must repeat mismatchRounds times (effect vars lag) before values are requested.
   */
  private compareGroup(name: string, g: GroupState): void {
    if (!this.bothConnected() || !this.simsConnected()) return;
    const [a, b] = this.slots.map((s) => g.reports.get(s.id));
    if (!a || !b) return;
    if (g.inFlight || this.pending.countInGroup(name) > 0) return;
    if (a.at < g.quietSince || b.at < g.quietSince) return;
    if (a.hash === b.hash) {
      g.mismatchCount = 0;
      g.diffVars = [];
      if (g.driftVars.length > 0) this.sendDrift(name, g, []);
      for (const d of [...this.desyncs.values()]) if (d.group === name && d.matchVar !== null) this.clearDesync(d.var, 'match');
      for (const v of g.observeSince.keys()) g.observeSince.delete(v);
      g.buttonSince.clear();
      return;
    }
    g.mismatchCount++;
    if (g.mismatchCount >= this.config.mismatchRounds) {
      g.mismatchCount = 0;
      this.requestSnapshot([name], 'mismatch');
    }
  }

  private requestSnapshot(groups: string[] | null, reason: RequestReason): void {
    if (!this.bothConnected()) {
      this.log.event('snapshot_request_skipped', { groups, reason, why: 'peer not connected' });
      return;
    }
    if (!this.simsConnected()) {
      // A disconnected sim has no values: not comparable (one amber line instead of missing_value desyncs).
      this.log.event('snapshot_request_skipped', { groups, reason, why: 'sim not connected' });
      return;
    }
    const id = `r${++this.requestCounter}`;
    const timer = setTimeout(() => this.expireRequest(id), this.config.snapshotRequestTimeoutMs);
    this.requests.set(id, { id, reason, groups, responses: new Map(), done: new Set(), timer });
    for (const name of groups ?? [...this.groups.keys()]) this.group(name).inFlight = id;
    this.emit(this.slots, 'snapshot_request', { request_id: id, groups, reason }, { replayable: false });
  }

  private expireRequest(id: string): void {
    const req = this.requests.get(id);
    if (!req) return;
    this.requests.delete(id);
    for (const g of this.groups.values()) if (g.inFlight === id) g.inFlight = null;
    const missing = (req.groups ?? []).filter((n) => !req.done.has(n));
    if (missing.length > 0) this.log.event('snapshot_request_expired', { request_id: id, missing });
  }

  private onSnapshotValues(slot: ClientSlot, body: SnapshotValuesBody): void {
    const req = this.requests.get(body.request_id);
    if (!req || req.done.has(body.group) || (req.groups && !req.groups.includes(body.group))) {
      this.log.event('snapshot_values_ignored', { client: slot.id, request_id: body.request_id, group: body.group });
      return;
    }
    const byClient = req.responses.get(body.group) ?? new Map<string, SnapshotValue[]>();
    byClient.set(slot.id, body.values);
    req.responses.set(body.group, byClient);
    if (!this.bothConnected() || !this.slots.every((s) => byClient.has(s.id))) return;

    req.done.add(body.group);
    const g = this.group(body.group);
    if (g.inFlight === req.id) g.inFlight = null;
    this.processGroup(req.id, req.reason, body.group, byClient);
    if (req.groups && req.groups.every((n) => req.done.has(n))) {
      clearTimeout(req.timer);
      this.requests.delete(req.id);
    }
  }

  private lastLogged(name: string): LoggedCommand | undefined {
    const rec = this.lastCommandByVar.get(name);
    if (!rec || !isAbsolute(rec.cls) || rec.value === undefined) return undefined;
    const pf = this.pfClientId;
    return { seq: rec.seq, value: rec.value, pfConfirmed: pf !== null && (rec.origin === pf || rec.okBy.has(pf)) };
  }

  /**
   * Builds the diff of one group and applies the PF safety rule, the loop guard and the freeze rule.
   * Owned values (fuel, payload) are never written: each pilot loads them in their own aircraft
   * (Fenix EFB). Differences are drift: reported, never corrected, never a desync.
   */
  private processGroup(requestId: string, reason: RequestReason, group: string, byClient: Map<string, SnapshotValue[]>): void {
    const pf = this.pfClientId ? this.slot(this.pfClientId) : undefined;
    const ref = pf ?? this.slots.find((s) => s.seat === 'host')!;
    const other = this.other(ref)!;
    const refValues = byClient.get(ref.id)!;
    const otherValues = byClient.get(other.id)!;
    const all = diffValues(refValues, otherValues);
    const differing = new Set(all.map((d) => d.var));
    const reported = new Set([...refValues, ...otherValues].map((v) => v.var));
    for (const v of [...refValues, ...otherValues]) if (v.class === 'owned' && v.step !== undefined) this.driftSteps.set(v.var, v.step);
    const g = this.group(group);
    const drift = all.filter((d) => d.class === 'owned');
    const diffs = all.filter((d) => d.class !== 'owned');
    g.diffVars = diffs.map((d) => d.var).sort();
    for (const d of [...this.desyncs.values()]) {
      if (d.matchVar !== null && reported.has(d.matchVar) && !differing.has(d.matchVar)) this.clearDesync(d.var, 'match');
    }
    const now = Date.now();
    for (const v of [...g.observeSince.keys()]) if (!differing.has(v)) g.observeSince.delete(v);
    for (const v of [...g.buttonSince.keys()]) if (!differing.has(v)) g.buttonSince.delete(v);
    const hasOwned = [...refValues, ...otherValues].some((v) => v.class === 'owned');
    if (hasOwned) this.sendDrift(group, g, drift);
    if (diffs.length === 0) {
      this.log.event(drift.length > 0 ? 'snapshot_drift_only' : 'snapshot_equal', { request_id: requestId, reason, group });
      return;
    }

    const raw: RawDecision[] = pf
      ? decideCorrections(diffs, (n) => this.lastLogged(n))
      : diffs.map((d) => buttonDecision(d) ?? { ...d, action: 'desync' as const, reason: 'no_pf' as const });

    const decisions: DiffDecision[] = [];
    const toCorrect: Extract<RawDecision, { action: 'correct' }>[] = [];
    const toRaise: { d: RawDecision; reason: DesyncReason; blocking: boolean; text: string }[] = [];

    for (const d of raw) {
      // A toggle lamp is reported under its button (the control the pilots press).
      const key = d.class === 'toggle' && d.button ? d.button : d.var;
      const open = this.desyncs.get(key);
      const base = { var: d.var, class: d.class, pf: d.pf, pm: d.pm };
      if (d.action === 'desync') {
        const tier = tierFor(d.class);
        if (open && open.reason === d.reason) {
          decisions.push({ ...base, action: 'held', reason: d.reason });
        } else if (tier === 'quiet' && now - (g.observeSince.get(d.var) ?? (g.observeSince.set(d.var, now), now)) < this.config.indicatorHoldMs) {
          // An indicator must differ for a while before it counts (it may just lag the input).
          decisions.push({ ...base, action: 'held', reason: d.reason });
        } else if ((d.class === 'pulse' || d.class === 'toggle') && !this.buttonPersists(g, d.var, now)) {
          // A snapshot taken mid-mirror must never raise a false red: 2 comparisons in a row or buttonHoldMs.
          decisions.push({ ...base, action: 'held', reason: d.reason });
        } else {
          decisions.push({ ...base, action: 'desync', reason: d.reason });
          toRaise.push({ d, reason: d.reason, blocking: true, text: desyncText(d.var, d.reason) });
        }
      } else if (open?.blocking) {
        decisions.push({ ...base, action: 'held', reason: open.reason });
      } else if (this.handover) {
        g.deferred = true;
        decisions.push({ ...base, action: 'deferred' });
      } else if (!this.guard.allows(d.var)) {
        decisions.push({ ...base, action: 'held', reason: 'correction_loop' });
        toRaise.push({
          d, reason: 'correction_loop', blocking: true,
          text: `${d.var}: needed more than ${this.config.correctionLoopMax} corrections within ${Math.round(this.config.correctionLoopWindowMs / 1000)} s (it keeps changing back). Not corrected any more until the values match or a pilot acts.`,
        });
      } else {
        decisions.push({
          ...base, action: 'correct', target: d.target, sides: d.sides, source: d.source,
          ...(d.source_seq !== undefined ? { source_seq: d.source_seq } : {}),
        });
        toCorrect.push(d);
      }
    }

    if (decisions.every((d) => d.action === 'held')) {
      this.log.event('snapshot_diff_held', { request_id: requestId, group, differences: decisions });
    } else {
      this.emit(this.slots, 'snapshot_diff', { request_id: requestId, group, pf_client_id: this.pfClientId, differences: decisions });
    }
    for (const r of toRaise) {
      const toggle = r.d.class === 'toggle' && r.d.button;
      this.raiseDesync({
        var: toggle ? r.d.button! : r.d.var, group, reason: r.reason, expected: r.d.pf, observed: r.d.pm, values: { pf: r.d.pf, pm: r.d.pm },
        message: toggle ? desyncText(r.d.button!, r.reason) : r.text,
      }, r.blocking, tierFor(r.d.class), r.d.var);
    }
    for (const d of toCorrect) this.sendCorrection(d, group, requestId, ref, other);
  }

  /**
   * Owned values that differ in one group, in their own units. Empty values = no drift any more.
   * Rate limited (driftShouldSend): a var that starts or stops drifting, or changes by more than its
   * step, is sent at once; smaller changes at most every driftMinIntervalMs.
   */
  private sendDrift(group: string, g: GroupState, drift: { var: string; pf: Scalar; pm: Scalar }[]): void {
    const values = drift
      .filter((d): d is { var: string; pf: number; pm: number } => typeof d.pf === 'number' && typeof d.pm === 'number')
      .map((d) => ({ var: d.var, pf: d.pf, pm: d.pm, delta: Math.round((d.pm - d.pf) * 1000) / 1000 }));
    g.driftVars = values.map((v) => v.var);
    const now = Date.now();
    const current = new Map(values.map((v) => [v.var, v.delta]));
    if (!driftShouldSend(g.driftSent, current, g.driftSentAt, now, (v) => this.driftSteps.get(v), this.config.driftMinIntervalMs)) return;
    g.driftSent = current;
    g.driftSentAt = now;
    for (const s of this.slots) if (s.state === 'connected') this.sendUnsequenced(s, 'drift', { group, values });
  }

  private sendCorrection(d: Extract<RawDecision, { action: 'correct' }>, group: string, requestId: string, pf: ClientSlot, pm: ClientSlot): void {
    this.guard.record(d.var);
    for (const side of d.sides) {
      const target = side === 'pf' ? pf : pm;
      const id = `corr:${this.seq + 1}`; // server namespace, cannot collide with client command ids
      const body: CorrectionBody = {
        id, var: d.var, class: d.class as CorrectionBody['class'], target: d.target, group, settle_ms: this.config.correctionSettleMs,
        source: d.source, ...(d.source_seq !== undefined ? { source_seq: d.source_seq } : {}), request_id: requestId, attempt: 0,
      };
      const sent = this.emit([target], 'correction', body);
      this.pending.start({
        kind: 'correction', id, seq: sent.seq, to: target.id, var: d.var, group, cls: d.class, expected: d.target,
        settleMs: this.config.correctionSettleMs, message: sent.env, side, retry: isAbsolute(d.class),
      }, target.state === 'connected');
    }
  }

  // ------------------------------------------------------------------ control token

  private denyControl(slot: ClientSlot, reason: 'peer_lost' | 'already_pf' | 'not_pf' | 'handover_in_progress', message: string): void {
    this.log.event('control_denied', { client: slot.id, reason });
    this.emit([slot], 'control_denied', { reason, desyncs: this.openDesyncs(), message });
  }

  /**
   * "I have control" from the PM. Granted immediately, like in a real cockpit: open
   * differences are listed in control_changed but never block. Only a lost PF needs an
   * explicit confirm.
   */
  private onControlRequest(slot: ClientSlot, body: ControlRequestBody): void {
    if (this.handover) return this.denyControl(slot, 'handover_in_progress', 'A control handover is already in progress.');
    if (this.pfClientId === slot.id) return this.denyControl(slot, 'already_pf', 'You already have control.');
    const peer = this.other(slot);
    const peerLost = !peer || peer.state !== 'connected';
    const confirmed = peerLost && (body.confirm ?? []).includes('@peer_lost');
    if (peerLost && !confirmed) {
      return this.denyControl(slot, 'peer_lost', 'The pilot flying is not connected. Confirm @peer_lost to take control anyway.');
    }
    this.grantControl(slot, 'take', confirmed ? ['@peer_lost'] : []);
  }

  /** "You have control" from the PF: the PM gets control with the same handover sequence. */
  private onControlGive(slot: ClientSlot): void {
    if (this.handover) return this.denyControl(slot, 'handover_in_progress', 'A control handover is already in progress.');
    if (this.pfClientId !== slot.id) return this.denyControl(slot, 'not_pf', 'Only the pilot flying can give control.');
    const peer = this.other(slot);
    if (!peer || peer.state !== 'connected') {
      return this.denyControl(slot, 'peer_lost', 'The other pilot is not connected; you keep control.');
    }
    this.grantControl(peer, 'give', []);
  }

  /** Groups and vars that differ right now. Informational only, for the new PF's UI. */
  private controlDifferences(): { groups: string[]; vars: string[] } {
    const groups = new Set<string>();
    const vars = new Set<string>();
    const [a, b] = this.slots;
    for (const [name, g] of this.groups) {
      const ha = a && g.reports.get(a.id);
      const hb = b && g.reports.get(b.id);
      const driftOnly = g.driftVars.length > 0 && g.diffVars.length === 0;
      if (ha && hb && ha.hash !== hb.hash && !driftOnly) groups.add(name);
      if (g.diffVars.length > 0) {
        groups.add(name);
        for (const v of g.diffVars) vars.add(v);
      }
    }
    for (const d of this.desyncs.values()) {
      vars.add(d.var);
      if (d.group) groups.add(d.group);
    }
    return { groups: [...groups].sort(), vars: [...vars].sort() };
  }

  private grantControl(slot: ClientSlot, kind: ControlKind, confirmed: ControlConfirm[]): void {
    const epoch = ++this.epoch;
    const prevPf = this.pfClientId;
    const timer = setTimeout(() => this.finishHandover(), this.config.freezeMs);
    this.handover = { epoch, kind, newPf: slot.id, prevPf, confirmed, timer };
    this.log.event('control_granted', { client: slot.id, epoch, kind, confirmed });
    this.emit(this.slots, 'control_handover', {
      epoch, kind, new_pf_client_id: slot.id, new_pf_seat: slot.seat, previous_pf_client_id: prevPf, freeze_ms: this.config.freezeMs,
    }, { replayable: false });
  }

  private finishHandover(): void {
    const h = this.handover;
    if (!h) return;
    this.handover = null;
    const next = this.slot(h.newPf);
    if (!next) {
      this.log.event('control_handover_aborted', { epoch: h.epoch, why: 'new PF left' });
      return;
    }
    this.pfClientId = next.id;
    const differences = this.controlDifferences();
    this.emit(this.slots, 'control_changed', {
      epoch: h.epoch, kind: h.kind, pf_client_id: next.id, pf_seat: next.seat, previous_pf_client_id: h.prevPf,
      confirmed: h.confirmed, differences, desyncs: this.openDesyncs(),
      last_frame: this.lastFrame && this.lastFrame.from === h.prevPf ? this.lastFrame : null,
    });
    // The previous PF's last position is only meaningful for this handover.
    this.lastFrame = null;
    if (this.controlAcks) clearTimeout(this.controlAcks.timer);
    this.controlAcks = { epoch: h.epoch, acked: new Set(), timer: setTimeout(() => this.checkControlAcks(), this.config.controlAckTimeoutMs) };

    const deferred = [...this.groups.entries()].filter(([, g]) => g.deferred).map(([name]) => name);
    for (const name of deferred) {
      const g = this.group(name);
      g.deferred = false;
      g.mismatchCount = 0;
    }
    if (deferred.length > 0) this.requestSnapshot(deferred, 'control');
  }

  private checkControlAcks(): void {
    const c = this.controlAcks;
    if (!c) return;
    this.controlAcks = null;
    for (const s of this.slots) {
      if (c.acked.has(s.id)) continue;
      this.emit(this.slots, 'notice', {
        kind: 'control_ack_missing', client_id: s.id,
        message: `The ${s.seat} side did not confirm the control change (epoch ${c.epoch}).`,
      });
    }
  }

  private onControlAck(slot: ClientSlot, body: ControlAckBody): void {
    const c = this.controlAcks;
    if (!c || c.epoch !== body.epoch) return;
    c.acked.add(slot.id);
    if (this.slots.every((s) => c.acked.has(s.id))) {
      clearTimeout(c.timer);
      this.controlAcks = null;
      this.log.event('control_acked', { epoch: c.epoch });
    }
  }

  // ------------------------------------------------------------------ stream

  private onStream(slot: ClientSlot, body: StreamBody): void {
    const st = slot.stream;
    const now = Date.now();
    if (this.handover || slot.id !== this.pfClientId) {
      st.dropped++;
      if (!this.handover && now - st.lastNoticeAt >= this.config.streamNoticeIntervalMs) {
        st.lastNoticeAt = now;
        this.emit([slot], 'notice', { kind: 'stream_dropped', message: 'Stream dropped: only the pilot flying may stream.' }, { replayable: false });
      }
      return;
    }
    if (now - st.windowStart >= 1000) {
      st.windowStart = now;
      st.windowCount = 0;
    }
    if (++st.windowCount > Math.ceil(this.config.streamMaxHz * 1.2)) {
      st.dropped++;
      return;
    }
    st.frames++;
    st.lastAt = now;
    if (!st.active) {
      st.active = true;
      this.log.event('stream_started', { client: slot.id, frame: body.frame });
    }
    const peer = this.other(slot);
    this.lastFrame = { frame: body.frame, data: body.data, from: slot.id };
    if (peer?.state === 'connected') this.sendUnsequenced(peer, 'stream', { frame: body.frame, data: body.data, from: slot.id });
  }

  // ------------------------------------------------------------------ liveness

  private onPing(slot: ClientSlot, body: PingBody): void {
    slot.clientRttMs = body.rtt_ms;
    this.sendUnsequenced(slot, 'pong', { nonce: body.nonce, t: body.t });
  }

  private onPong(slot: ClientSlot, body: PongBody): void {
    const now = Date.now();
    if (body.t > now) return;
    slot.lastPongAt = now;
    slot.rttMs = now - body.t;
  }

  /** Every heartbeat: lost detection in both directions, pings, stream idle. */
  private tick(): void {
    const now = Date.now();
    for (const slot of [...this.slots]) {
      if (slot.state !== 'connected') continue;
      if (now - slot.lastRxAt > this.config.lostAfterMs) {
        this.markLost(slot, 'no_messages');
      } else if (now - slot.lastPongAt > this.config.lostAfterMs) {
        this.markLost(slot, 'no_pong');
      } else {
        this.sendUnsequenced(slot, 'ping', { nonce: ++slot.pingNonce, t: now, rtt_ms: slot.rttMs });
      }
      const st = slot.stream;
      if (st.active && now - st.lastAt > this.config.streamIdleMs) {
        st.active = false;
        this.log.event('stream_stopped', { client: slot.id, frames: st.frames, dropped: st.dropped });
      }
    }
  }

  private summary(): void {
    this.log.event('summary', {
      clients: this.slots.map((s) => ({
        client: s.id, seat: s.seat, role: this.role(s), state: s.state, rtt_ms: s.rttMs, client_rtt_ms: s.clientRttMs,
        stream_frames: s.stream.frames, stream_dropped: s.stream.dropped,
      })),
      pending_groups: [...this.groups.keys()].filter((g) => this.pending.countInGroup(g) > 0),
      open_desyncs: this.openDesyncs(),
    });
  }

  private markLost(slot: ClientSlot, reason: 'closed' | 'no_messages' | 'no_pong'): void {
    if (slot.state === 'lost') return;
    slot.state = 'lost';
    const conn = slot.conn;
    slot.conn = null;
    if (conn) {
      conn.slot = undefined;
      conn.session = undefined;
      conn.terminate();
    }
    this.pending.pauseFor(slot.id);
    this.resetSimState(slot);
    // Old hashes from before the outage must not be compared after a resume.
    for (const g of this.groups.values()) g.reports.delete(slot.id);
    this.log.event('client_lost', { client: slot.id, reason });
    const peer = this.other(slot);
    if (peer) this.emit([peer], 'peer_status', { client_id: slot.id, seat: slot.seat, role: this.role(slot), state: 'lost', reason });
    slot.expiryTimer = setTimeout(() => this.removeSlot(slot, 'expired'), this.config.sessionTtlMs);
  }

  private removeSlot(slot: ClientSlot, state: 'left' | 'expired'): void {
    if (!this.slots.includes(slot)) return;
    const role = this.role(slot);
    if (slot.expiryTimer) clearTimeout(slot.expiryTimer);
    this.slots.splice(this.slots.indexOf(slot), 1);
    const conn = slot.conn;
    slot.conn = null;
    if (conn) {
      conn.slot = undefined;
      conn.session = undefined;
      conn.terminate();
    }
    this.pending.cancelWhere((e) => e.to === slot.id);
    this.resetSimState(slot);
    for (const [id, c] of this.effectChecks) if (c.origin === slot.id || c.receiver === slot.id) this.dropEffectCheck(id);
    this.replay.forget(slot.id);
    for (const g of this.groups.values()) g.reports.delete(slot.id);
    if (this.pfClientId === slot.id) this.pfClientId = null;
    if (this.handover?.newPf === slot.id) {
      clearTimeout(this.handover.timer);
      this.handover = null;
    }
    this.log.event(state === 'left' ? 'client_left' : 'client_expired', { client: slot.id });
    const peer = this.slots[0];
    if (peer) this.emit([peer], 'peer_status', { client_id: slot.id, seat: slot.seat, role, state });
    if (this.slots.length === 0) this.end('empty');
  }

  /** Stops all timers. Sockets are closed by the caller (server shutdown) or already gone. */
  end(why: string): void {
    if (this.ended) return;
    this.ended = true;
    clearInterval(this.tickTimer);
    clearInterval(this.summaryTimer);
    this.pending.clear();
    for (const r of this.requests.values()) clearTimeout(r.timer);
    this.requests.clear();
    if (this.handover) clearTimeout(this.handover.timer);
    if (this.controlAcks) clearTimeout(this.controlAcks.timer);
    for (const o of this.overrides.values()) if (o.timer) clearTimeout(o.timer);
    for (const c of this.effectChecks.values()) clearTimeout(c.timer);
    this.effectChecks.clear();
    for (const s of this.slots) {
      if (s.expiryTimer) clearTimeout(s.expiryTimer);
      s.conn?.terminate();
    }
    this.log.event('session_end', { why });
    this.onEnd(this);
  }

  // ------------------------------------------------------------------ helpers

  private addSlot(conn: Conn, seat: Seat, preflight: Preflight, name: string | undefined): ClientSlot {
    const now = Date.now();
    const slot: ClientSlot = {
      id: newClientId(), seat, token: newResumeToken(), preflight, conn: null, state: 'connected',
      lastRxAt: now, lastPongAt: now, rttMs: null, clientRttMs: null, pingNonce: 0, expiryTimer: null,
      stream: { frames: 0, dropped: 0, active: false, lastAt: 0, windowStart: 0, windowCount: 0, lastNoticeAt: -Infinity },
      simConnected: true,
      ...(name ? { name } : {}),
    };
    this.slots.push(slot);
    this.attach(slot, conn);
    this.log.event('client_added', { client: slot.id, seat, name: name ?? null, preflight });
    return slot;
  }

  private attach(slot: ClientSlot, conn: Conn): void {
    const now = Date.now();
    slot.conn = conn;
    slot.state = 'connected';
    slot.lastRxAt = now;
    slot.lastPongAt = now;
    conn.slot = slot;
    conn.session = this;
  }

  private other(slot: ClientSlot): ClientSlot | undefined {
    return this.slots.find((s) => s !== slot);
  }

  private bothConnected(): boolean {
    return this.slots.length === 2 && this.slots.every((s) => s.state === 'connected');
  }

  private simsConnected(): boolean {
    return this.slots.every((s) => s.simConnected);
  }

  // ------------------------------------------------------------------ advisories (amber lines)

  /** Sends one amber line to both pilots (latest body per key wins); inactive ones are forgotten. */
  private setAdvisory(body: AdvisoryBody): void {
    if (body.active) this.advisories.set(body.key, body);
    else if (!this.advisories.delete(body.key)) return;
    this.emit(this.slots, 'advisory', body);
  }

  private openAdvisories(): AdvisoryBody[] {
    return [...this.advisories.values()];
  }

  /** The bridge's sim connected or disconnected. While disconnected nothing is compared. */
  private onSimState(slot: ClientSlot, body: SimStateBody): void {
    if (slot.simConnected === body.connected) return;
    slot.simConnected = body.connected;
    this.log.event('sim_state', { client: slot.id, connected: body.connected });
    const key = `sim_disconnected:${slot.seat}`;
    if (!body.connected) {
      for (const g of this.groups.values()) {
        g.reports.delete(slot.id);
        g.mismatchCount = 0;
        g.observeSince.clear();
      }
      this.setAdvisory({
        key, kind: 'sim_disconnected', active: true, tier: 'amber', seat: slot.seat, at: Date.now(),
        message: `The ${slot.seat} simulator is not connected: its panels are not compared until it is back.`,
      });
      return;
    }
    this.setAdvisory({ key, kind: 'sim_disconnected', active: false, tier: 'amber', seat: slot.seat, at: Date.now(), message: `The ${slot.seat} simulator is connected again.` });
    if (this.simsConnected()) this.requestSnapshot(null, 'resync');
  }

  /** Lost or gone: its sim state is unknown, the peer_status line says so instead. */
  private resetSimState(slot: ClientSlot): void {
    if (slot.simConnected) return;
    slot.simConnected = true;
    this.setAdvisory({
      key: `sim_disconnected:${slot.seat}`, kind: 'sim_disconnected', active: false, tier: 'amber', seat: slot.seat, at: Date.now(),
      message: `The ${slot.seat} side left or was lost.`,
    });
  }

  /** A pilot moved a control that is watched read-only (not synced yet). One line per control. */
  /** An edge button differs in this comparison: true once it differed 2 times in a row or for buttonHoldMs. */
  private buttonPersists(g: GroupState, name: string, now: number): boolean {
    const seen = g.buttonSince.get(name);
    if (!seen) {
      g.buttonSince.set(name, { at: now, rounds: 1 });
      return false;
    }
    seen.rounds++;
    return seen.rounds >= 2 || now - seen.at >= this.config.buttonHoldMs;
  }

  /**
   * A receiver released an edge button that Crosscheck had pressed down in its sim, because the up never
   * arrived (protocol.md section 5): the only write no pilot made. Always red, to both pilots; it clears
   * when the button state matches again in a snapshot.
   */
  private onSafetyRelease(slot: ClientSlot, body: SafetyReleaseBody): void {
    this.log.event('safety_release', { client: slot.id, var: body.var, cause: body.cause, observed: body.observed });
    const why = body.cause === 'cap' ? 'no release arrived within 30 s'
      : body.cause === 'link_lost' ? `the ${slot.seat}'s connection was lost` : 'the other pilot was lost';
    this.raiseDesync({
      var: body.var, group: body.group, reason: 'safety_release', expected: null, observed: body.observed, client_id: slot.id,
      message: `${body.var}: Crosscheck released this button in the ${slot.seat}'s cockpit because ${why}. Check it on both sides.`,
    }, false, 'red', body.var);
  }

  private onUnsyncedInput(slot: ClientSlot, body: UnsyncedInputBody): void {
    const key = `not_synced:${body.var}`;
    const prev = this.advisories.get(key);
    this.setAdvisory({
      key, kind: 'not_synced', active: true, tier: 'amber', var: body.var, label: body.label, seat: slot.seat, at: Date.now(),
      count: (prev?.count ?? 0) + 1, message: `${body.label} (${body.var}) moved on the ${slot.seat} side: not synced yet.`,
    });
  }

  /** Overrides on one var: fightCount within fightWindowMs → "hardware fighting" until it calms down. */
  private recordOverride(name: string, slot: ClientSlot): void {
    const now = Date.now();
    const o = this.overrides.get(name) ?? { times: [], timer: null };
    o.times = [...o.times.filter((t) => now - t < this.config.fightWindowMs), now];
    this.overrides.set(name, o);
    const key = `hardware_fighting:${name}`;
    if (o.times.length < this.config.fightCount && !this.advisories.has(key)) return;
    this.setAdvisory({
      key, kind: 'hardware_fighting', active: true, tier: 'amber', var: name, seat: slot.seat, at: now, count: o.times.length,
      message: `${name} keeps changing back and forth between the two sims (${o.times.length} overrides in ${Math.round(this.config.fightWindowMs / 1000)} s): both hardware controls may be setting it.`,
    });
    if (o.timer) clearTimeout(o.timer);
    o.timer = setTimeout(() => {
      o.timer = null;
      this.overrides.delete(name);
      this.setAdvisory({ key, kind: 'hardware_fighting', active: false, tier: 'amber', var: name, seat: slot.seat, at: Date.now(), message: `${name}: no more overrides.` });
    }, this.config.fightWindowMs);
  }

  // ------------------------------------------------------------------ pulse effect check

  /** Compares the effect var of both sims after a pulse: red only if they differ (both unchanged is fine). */
  private startEffectCheck(body: CommandBody, origin: ClientSlot, receiver: ClientSlot): void {
    const effect = body.effect!;
    const wait = body.settle_ms + 2 * this.config.ackMarginMs + (origin.rttMs ?? 0) + (receiver.rttMs ?? 0);
    const check: EffectCheck = {
      id: body.id, var: body.var, group: body.group, effect, origin: origin.id, receiver: receiver.id,
      timer: setTimeout(() => this.completeEffectCheck(check, true), wait),
    };
    this.effectChecks.set(body.id, check);
  }

  private onEffectReport(slot: ClientSlot, body: EffectReportBody): void {
    const check = this.effectChecks.get(body.id);
    if (!check || check.origin !== slot.id) {
      this.log.event('effect_report_unmatched', { client: slot.id, id: body.id });
      return;
    }
    check.sender = body.value;
    this.completeEffectCheck(check, false);
  }

  private dropEffectCheck(id: string): void {
    const check = this.effectChecks.get(id);
    if (!check) return;
    clearTimeout(check.timer);
    this.effectChecks.delete(id);
  }

  private completeEffectCheck(check: EffectCheck, timedOut: boolean): void {
    if (!timedOut && (check.sender === undefined || check.received === undefined)) return;
    this.dropEffectCheck(check.id);
    const sender = check.sender ?? null;
    const received = check.received ?? null;
    const known = sender !== null && received !== null;
    if (known && effectsMatch(sender, received, check.effect.expect)) {
      this.log.event('effect_ok', { id: check.id, var: check.var, effect: check.effect.var, value: sender });
      return;
    }
    const origin = this.slot(check.origin);
    const receiver = this.slot(check.receiver);
    if (!origin || !receiver || receiver.state !== 'connected' || origin.state !== 'connected') {
      // A side left or was lost: that is shown as such; the result cannot be compared.
      this.log.event('effect_check_dropped', { id: check.id, var: check.var, why: 'side not connected' });
      return;
    }
    const values = this.role(origin) === 'pf' ? { pf: sender, pm: received } : { pf: received, pm: sender };
    const message = known
      ? `${check.var}: pressed on the ${origin.seat} side, but ${check.effect.var} shows ${String(received)} on the ${receiver.seat} side and ${String(sender)} on the ${origin.seat} side.`
      : `${check.var}: pressed on the ${origin.seat} side; the result (${check.effect.var}) is unknown on the ${sender === null ? origin.seat : receiver.seat} side.`;
    this.raiseDesync({
      var: check.var, group: check.group, reason: 'effect_mismatch', expected: sender, observed: received, values,
      command_id: check.id, client_id: check.receiver, message,
    }, false, 'red', check.effect.var);
  }

  private role(slot: ClientSlot): Role {
    return slot.id === this.pfClientId ? 'pf' : 'pm';
  }

  private peerInfo(slot: ClientSlot): PeerInfo {
    return { client_id: slot.id, seat: slot.seat, role: this.role(slot), state: slot.state, ...(slot.name ? { name: slot.name } : {}) };
  }

  private timing(): Timing {
    return {
      heartbeat_ms: this.config.heartbeatMs, lost_after_ms: this.config.lostAfterMs,
      session_ttl_ms: this.config.sessionTtlMs, snapshot_interval_ms: this.config.snapshotIntervalMs,
    };
  }

  /**
   * Sends a logged message to targets under one new seq (or the given one). Replayable
   * types go into the replay log, also for lost targets (they get them on resume).
   */
  private emit(targets: ClientSlot[], type: string, body: object, opts: { seq?: number; replayable?: boolean } = {}): { seq: number; env: Envelope } {
    const replayable = opts.replayable ?? REPLAYABLE.has(type);
    const seq = opts.seq ?? ++this.seq;
    const env: Envelope = { v: PROTOCOL_VERSION, type, session: this.id, seq, ts: Date.now(), body: body as Record<string, unknown> };
    for (const t of targets) {
      if (replayable) this.replay.add(seq, t.id, env);
      if (t.state === 'connected') this.sendRaw(t, env);
    }
    this.log.write({ dir: 'out', to: targets.map((t) => t.id), msg: env });
    return { seq, env };
  }

  private sendUnsequenced(slot: ClientSlot, type: string, body: object): void {
    const env: Envelope = { v: PROTOCOL_VERSION, type, session: this.id, ts: Date.now(), body: body as Record<string, unknown> };
    this.sendRaw(slot, env);
    if (!NOT_LOGGED.has(type)) this.log.write({ dir: 'out', to: [slot.id], msg: env });
  }

  private sendError(slot: ClientSlot, code: string, message: string): void {
    this.sendUnsequenced(slot, 'error', { code, message });
  }

  private sendRaw(slot: ClientSlot, env: Envelope): void {
    if (this.config.validateOutgoing) {
      const result = this.validator.validate(env);
      if (!result.ok) throw new Error(`outgoing ${env.type} violates the schema: ${result.details.join('; ') || result.message}`);
    }
    const conn = slot.conn;
    if (conn?.isOpen()) conn.send(JSON.stringify(env));
  }
}

/** Only an indicator (observe var) may be quiet. Everything a pilot sets is red. */
export function tierFor(cls: string): Tier {
  return cls === 'observe' ? 'quiet' : 'red';
}

/** Effect values of both sims after a pulse: equal (same), or equal odd/even (parity). */
export function effectsMatch(a: Scalar, b: Scalar, expect: 'same' | 'parity'): boolean {
  if (typeof a === 'number' && typeof b === 'number') {
    return expect === 'parity' ? (Math.round(a) & 1) === (Math.round(b) & 1) : Math.abs(a - b) <= 1e-6;
  }
  return a === b;
}

/**
 * Drift rate limit. Send at once when a var starts or stops drifting, or its delta moved by more
 * than its step since it was last sent; any other change at most every minIntervalMs. Without a
 * step every change waits for the interval.
 */
export function driftShouldSend(
  sent: ReadonlyMap<string, number>, current: ReadonlyMap<string, number>, sentAt: number, now: number,
  stepOf: (name: string) => number | undefined, minIntervalMs: number,
): boolean {
  let changed = false;
  for (const name of sent.keys()) if (!current.has(name)) return true;
  for (const [name, delta] of current) {
    const last = sent.get(name);
    if (last === undefined) return true;
    if (Math.abs(delta - last) > (stepOf(name) ?? Infinity)) return true;
    if (delta !== last) changed = true;
  }
  return changed && now - sentAt >= minIntervalMs;
}

function desyncText(name: string, reason: DesyncReason): string {
  switch (reason) {
    case 'press_mismatch': return `${name}: held down in one cockpit only. Never pressed automatically.`;
    case 'toggle_mismatch': return `${name}: its light is on in one cockpit and off in the other. If nothing above it differs, press it on one side.`;
    case 'observe_mismatch': return `${name}: the sims show different values (output, never written).`;
    case 'log_conflict': return `${name}: neither sim has the last pilot command's value; not corrected.`;
    case 'missing_value': return `${name}: value unknown on one side; not corrected.`;
    case 'no_pf': return `${name}: differs and there is no pilot flying to take as reference.`;
    default: return `${name}: out of sync (${reason}).`;
  }
}
