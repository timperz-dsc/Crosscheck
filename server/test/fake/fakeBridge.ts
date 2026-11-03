import { WebSocket } from 'ws';
import {
  isAbsolute,
  type AckBody, type CommandBody, type CorrectionBody, type Envelope, type Preflight, type Timing,
} from '../../src/protocol/types.js';
import { sharedValidator } from '../../src/protocol/validate.js';
import { sameValue } from '../../src/session/corrections.js';
import type { FakeSim } from './fakeSim.js';

export const PREFLIGHT: Preflight = {
  bridge_version: '0.5.0',
  profile_id: 'fenix-a320',
  profile_hash: 'sha256:7f3a9c1e2b4d5f60',
  aircraft: { package_version: '3.0.0.7613', app_version: '3.0.0.330' },
  airac: '2611',
};

export interface BridgeOptions {
  url: string;
  name: string;
  sim: FakeSim;
  preflight?: Preflight;
  /** Added in both directions (order is kept). */
  latencyMs?: number;
  /** Send snapshot hashes on a timer (default) or only via sendHashes(). */
  autoHash?: boolean;
  /** Override the hash interval the server announces. */
  hashIntervalMs?: number;
}

type Waiter = { type: string; pred: (m: Envelope) => boolean; resolve: (m: Envelope) => void };

/**
 * An in-process stand-in for the C# bridge. Talks protocol v1 over a real WebSocket and
 * follows the client obligations from docs/protocol.md (seq order, superseded commands,
 * dedupe by id, acks, snapshots, control acks). Faults can be switched on per test.
 */
export class FakeBridge {
  readonly sim: FakeSim;
  readonly name: string;
  readonly received: Envelope[] = [];
  /** Schema violations in messages from the server. Must stay empty. */
  readonly invalid: string[] = [];

  sessionId: string | null = null;
  clientId = '';
  token = '';
  seat = '';
  joinCode = '';
  pfClientId: string | null = null;
  timing: Timing | null = null;

  /** Highest envelope seq seen (for resume). */
  lastSeq = 0;
  /** Highest pilot-command seq processed (snapshot_hash.applied_seq). */
  appliedSeq = 0;
  private readonly lastSeqByVar = new Map<string, number>();
  private readonly pendingOwn = new Map<string, string>();
  private readonly ackCache = new Map<string, AckBody>();
  private counter = 0;
  private nonce = 0;
  private rttMs: number | null = null;

  // ---- faults ----
  /** Drop the next n incoming commands (as if lost). */
  dropCommands = 0;
  /** Drop the next n incoming corrections. */
  dropCorrections = 0;
  /** Apply commands but never send acks. */
  dropAcks = false;
  /** Vars SimConnect refuses to write in this sim (CRO-40): acked ok false with reason cannot_write, never written. */
  readonly cannotWrite = new Set<string>();
  /** Receiver side of the effect check (protocol 4): report the own lamp after settle. false = never (lost report). */
  reportEffects = true;
  /** Socket stays open, but nothing is sent any more (one-way stall). */
  mute = false;
  /** The simulator is connected. While false: no snapshot answers and no hashes (like the real bridge). */
  simConnected = true;
  /** Socket stays open, but nothing received is processed (other direction stalled). */
  deaf = false;
  /** Ids of commands that were dropped, with their attempt numbers. */
  readonly droppedCommands: { id: string; attempt: number }[] = [];

  private ws: WebSocket | null = null;
  private readonly waiters: Waiter[] = [];
  private timers: NodeJS.Timeout[] = [];
  private readonly latencyMs: number;
  private readonly autoHash: boolean;

  constructor(private readonly opts: BridgeOptions) {
    this.sim = opts.sim;
    this.name = opts.name;
    this.latencyMs = opts.latencyMs ?? 0;
    this.autoHash = opts.autoHash ?? true;
  }

  get role(): 'pf' | 'pm' {
    return this.pfClientId === this.clientId ? 'pf' : 'pm';
  }

  // ------------------------------------------------------------ connection

  async connect(): Promise<void> {
    const ws = new WebSocket(this.opts.url);
    this.ws = ws;
    ws.on('message', (data) => this.onRaw(data.toString()));
    ws.on('close', () => {
      if (this.ws === ws) this.stopTimers();
    });
    await new Promise<void>((resolve, reject) => {
      ws.once('open', () => resolve());
      ws.once('error', reject);
    });
  }

  async create(): Promise<Envelope> {
    this.send('create', { preflight: this.opts.preflight ?? PREFLIGHT, name: this.name }, null);
    return this.waitFor('created');
  }

  /** Resolves with joined or join_rejected. */
  async join(code: string): Promise<Envelope> {
    this.send('join', { join_code: code, preflight: this.opts.preflight ?? PREFLIGHT, name: this.name }, null);
    return this.waitForAny(['joined', 'join_rejected']);
  }

  /** Drops the connection without a goodbye. */
  disconnect(): void {
    this.stopTimers();
    const ws = this.ws;
    this.ws = null;
    ws?.terminate();
  }

  /** New socket + resume with the last seen seq. Resolves with resumed or error. */
  async resume(): Promise<Envelope> {
    this.disconnect();
    this.mute = false;
    this.deaf = false;
    const from = this.received.length;
    await this.connect();
    this.send('resume', { client_id: this.clientId, resume_token: this.token, last_seq: this.lastSeq });
    return this.waitForAny(['resumed', 'error'], { from });
  }

  leave(): void {
    this.send('leave', {});
  }

  close(): void {
    this.disconnect();
  }

  // ------------------------------------------------------------ pilot actions

  /** Pilot flips a switch / sets a knob: local write, then command. Returns the command id. */
  set(name: string, value: number, settleMs = 20): string {
    const def = this.sim.defs.get(name)!;
    this.sim.write(name, value, 'pilot');
    const id = this.nextId();
    this.pendingOwn.set(id, name);
    this.send('command', { id, var: name, class: def.cls, value, group: def.group, settle_ms: settleMs });
    return id;
  }

  turn(name: string, delta: number, settleMs = 20): string {
    const def = this.sim.defs.get(name)!;
    this.sim.turn(name, delta, 'pilot');
    const id = this.nextId();
    this.send('command', { id, var: name, class: 'delta', delta, group: def.group, settle_ms: settleMs });
    return id;
  }

  press(name: string, settleMs = 20): string {
    const def = this.sim.defs.get(name)!;
    this.sim.press(name, 'pilot');
    const id = this.nextId();
    this.send('command', { id, var: name, class: 'pulse', value: 1, group: def.group, settle_ms: settleMs });
    return id;
  }

  /**
   * A press with an effect check: the command carries effect, and the own effect value is reported
   * reportAfterMs later (null = never report).
   */
  pressWithEffect(name: string, opts: { expect?: 'same' | 'parity'; settleMs?: number; reportAfterMs?: number | null } = {}): string {
    const def = this.sim.defs.get(name)!;
    const effectVar = def.toggles!;
    this.sim.press(name, 'pilot');
    const id = this.nextId();
    const settleMs = opts.settleMs ?? 20;
    this.send('command', {
      id, var: name, class: 'pulse', value: 1, group: def.group, settle_ms: settleMs, effect: { var: effectVar, expect: opts.expect ?? 'same' },
    });
    const after = opts.reportAfterMs === undefined ? settleMs : opts.reportAfterMs;
    if (after !== null) setTimeout(() => this.send('effect_report', { id, var: effectVar, value: this.sim.get(effectVar) }), after);
    return id;
  }

  /** The simulator connects or disconnects (sim_state). */
  setSim(connected: boolean): void {
    this.simConnected = connected;
    this.send('sim_state', { connected });
  }

  /** The pilot moved a control that is not synced (watched read-only). */
  unsyncedInput(name: string, label: string): void {
    this.send('unsynced_input', { var: name, label });
  }

  requestControl(confirm?: string[]): void {
    this.send('control_request', confirm ? { confirm } : {});
  }

  /** "You have control": the PF hands control to the PM. */
  giveControl(): void {
    this.send('control_give', {});
  }

  stream(frame: number, data: Record<string, number>): void {
    this.send('stream', { frame, data });
  }

  sendHashes(): void {
    if (!this.simConnected) return;
    for (const group of this.sim.groups()) {
      const loose = this.sim.loose(group);
      this.send('snapshot_hash', { group, hash: this.sim.hash(group), applied_seq: this.appliedSeq, ...(loose.length > 0 ? { loose } : {}) });
    }
  }

  /** Low-level send (tests use it for malformed or custom messages). */
  send(type: string, body: object, session: string | null = this.sessionId): void {
    const env = { v: 5, type, session, ts: Date.now(), body };
    const check = sharedValidator().validate(env);
    if (!check.ok) throw new Error(`fake bridge would send an invalid ${type}: ${check.message}`);
    this.sendText(JSON.stringify(env));
  }

  sendText(text: string): void {
    if (this.mute) return;
    const ws = this.ws;
    const go = () => {
      if (ws && ws.readyState === ws.OPEN) ws.send(text);
    };
    if (this.latencyMs > 0) setTimeout(go, this.latencyMs);
    else go();
  }

  // ------------------------------------------------------------ waiting

  mark(): number {
    return this.received.length;
  }

  messages(type: string, pred: (m: Envelope) => boolean = () => true, from = 0): Envelope[] {
    return this.received.slice(from).filter((m) => m.type === type && pred(m));
  }

  waitFor(type: string, pred: (m: Envelope) => boolean = () => true, opts: { from?: number; timeoutMs?: number } = {}): Promise<Envelope> {
    return this.waitForAny([type], { ...opts, pred });
  }

  waitForAny(types: string[], opts: { from?: number; timeoutMs?: number; pred?: (m: Envelope) => boolean } = {}): Promise<Envelope> {
    const pred = opts.pred ?? (() => true);
    const found = this.received.slice(opts.from ?? 0).find((m) => types.includes(m.type) && pred(m));
    if (found) return Promise.resolve(found);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.waiters.splice(this.waiters.indexOf(waiter), 1);
        reject(new Error(`${this.name}: timed out waiting for ${types.join('|')}`));
      }, opts.timeoutMs ?? 5000);
      const waiter: Waiter = {
        type: types.join('|'),
        pred: (m) => types.includes(m.type) && pred(m),
        resolve: (m) => {
          clearTimeout(timer);
          resolve(m);
        },
      };
      this.waiters.push(waiter);
    });
  }

  // ------------------------------------------------------------ inbound

  private onRaw(text: string): void {
    if (this.deaf) return;
    if (this.latencyMs > 0) setTimeout(() => this.handle(text), this.latencyMs);
    else this.handle(text);
  }

  private handle(text: string): void {
    const msg = JSON.parse(text) as Envelope;
    const check = sharedValidator().validate(msg);
    if (!check.ok) this.invalid.push(`${msg.type}: ${check.message}`);
    this.received.push(msg);
    if (msg.seq !== undefined) this.lastSeq = Math.max(this.lastSeq, msg.seq);
    const body = msg.body as Record<string, unknown>;

    switch (msg.type) {
      case 'created':
      case 'joined':
      case 'resumed':
        this.onSession(msg);
        break;
      case 'command': this.onCommand(msg as unknown as Envelope<'command', CommandBody>); break;
      case 'correction': this.onCorrection(msg as unknown as Envelope<'correction', CorrectionBody>); break;
      case 'accepted': {
        const name = this.pendingOwn.get(body.id as string);
        this.pendingOwn.delete(body.id as string);
        if (name && msg.seq) this.lastSeqByVar.set(name, Math.max(this.lastSeqByVar.get(name) ?? 0, msg.seq));
        if (msg.seq) this.appliedSeq = Math.max(this.appliedSeq, msg.seq);
        break;
      }
      case 'snapshot_request': {
        if (!this.simConnected) break;
        const groups = (body.groups as string[] | null) ?? this.sim.groups();
        for (const group of groups) {
          if (!this.sim.groups().includes(group)) continue;
          this.send('snapshot_values', { request_id: body.request_id, group, values: this.sim.snapshot(group) });
        }
        break;
      }
      case 'control_changed':
        this.pfClientId = body.pf_client_id as string;
        this.send('control_ack', { epoch: body.epoch });
        break;
      case 'ping':
        this.send('pong', { nonce: body.nonce, t: body.t });
        break;
      case 'pong':
        this.rttMs = Date.now() - (body.t as number);
        break;
      default:
        break;
    }

    for (const w of [...this.waiters]) {
      if (w.pred(msg)) {
        this.waiters.splice(this.waiters.indexOf(w), 1);
        w.resolve(msg);
      }
    }
  }

  private onSession(msg: Envelope): void {
    const b = msg.body as Record<string, unknown>;
    this.sessionId = b.session_id as string;
    this.clientId = b.client_id as string;
    if (typeof b.resume_token === 'string') this.token = b.resume_token;
    if (typeof b.join_code === 'string') this.joinCode = b.join_code;
    this.seat = b.seat as string;
    this.pfClientId = (b.pf_client_id as string | null) ?? null;
    this.timing = b.config as Timing;
    this.startTimers();
  }

  private onCommand(msg: Envelope<'command', CommandBody>): void {
    const b = msg.body;
    const seq = msg.seq ?? 0;
    if (this.dropCommands > 0) {
      this.dropCommands--;
      this.droppedCommands.push({ id: b.id, attempt: b.attempt ?? 0 });
      return;
    }
    const cached = this.ackCache.get(b.id);
    if (cached) return this.ack(cached);

    let ack: AckBody;
    if (isAbsolute(b.class)) {
      const ownPending = [...this.pendingOwn.values()].includes(b.var);
      if (ownPending || (this.lastSeqByVar.get(b.var) ?? 0) > seq) {
        ack = { id: b.id, ok: true, observed: this.sim.get(b.var), superseded: true };
      } else if (this.cannotWrite.has(b.var) && !sameValue(this.sim.get(b.var), b.value!)) {
        ack = { id: b.id, ok: false, observed: this.sim.get(b.var), reason: 'cannot_write' };
      } else {
        this.sim.write(b.var, b.value!, 'remote');
        const observed = this.sim.get(b.var);
        ack = { id: b.id, ok: sameValue(observed, b.value!), observed };
        this.lastSeqByVar.set(b.var, seq);
      }
    } else if (b.class === 'delta') {
      const ok = this.sim.turn(b.var, b.delta!, 'remote');
      ack = { id: b.id, ok, observed: this.sim.display(b.var) };
    } else {
      // An up edge releases the button: no new press (the lamp toggles on the down edge only).
      const ok = b.edge === 'up' ? true : this.sim.press(b.var, 'remote');
      ack = { id: b.id, ok, observed: b.value ?? 1 };
      // Protocol 4: the receiver reports its own lamp after settle (one report per burst in the real bridge).
      const effect = b.effect;
      if (effect && this.reportEffects) setTimeout(() => this.send('effect_report', { id: b.id, var: effect.var, value: this.sim.get(effect.var) }), b.settle_ms);
    }
    this.appliedSeq = Math.max(this.appliedSeq, seq);
    this.ackCache.set(b.id, ack);
    this.ack(ack);
  }

  private onCorrection(msg: Envelope<'correction', CorrectionBody>): void {
    const b = msg.body;
    if (this.dropCorrections > 0) {
      this.dropCorrections--;
      return;
    }
    const cached = this.ackCache.get(b.id);
    if (cached) return this.ack(cached);
    // Protocol 4: corrections are absolute values only (encoders and push buttons are never corrected).
    this.sim.write(b.var, b.target, 'correction');
    const observed = this.sim.get(b.var);
    const ack: AckBody = { id: b.id, ok: sameValue(observed, b.target), observed };
    this.ackCache.set(b.id, ack);
    this.ack(ack);
  }

  private ack(ack: AckBody): void {
    if (!this.dropAcks) this.send('ack', ack);
  }

  // ------------------------------------------------------------ timers

  private startTimers(): void {
    this.stopTimers();
    const t = this.timing!;
    this.timers.push(setInterval(() => {
      this.send('ping', { nonce: ++this.nonce, t: Date.now(), rtt_ms: this.rttMs });
    }, t.heartbeat_ms));
    if (this.autoHash) this.timers.push(setInterval(() => this.sendHashes(), this.opts.hashIntervalMs ?? t.snapshot_interval_ms));
  }

  private stopTimers(): void {
    for (const t of this.timers) clearInterval(t);
    this.timers = [];
  }

  nextId(): string {
    return `${this.name}-${++this.counter}`;
  }
}
