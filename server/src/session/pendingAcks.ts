import type { AckBody, Envelope, Role, Scalar } from '../protocol/types.js';

export interface PendingEntry {
  kind: 'command' | 'correction';
  id: string;
  seq: number;
  /** Receiver client id. */
  to: string;
  /** Origin client id (commands only). */
  origin?: string;
  var: string;
  group: string;
  cls: string;
  expected: Scalar;
  delta?: number;
  settleMs: number;
  /** The message as first sent (attempt 0). Re-sends copy it with a new attempt number. */
  message: Envelope;
  attempt: number;
  lastAck: AckBody | null;
  timer: NodeJS.Timeout | null;
  paused: boolean;
  /** Corrections: which side the receiver was when the correction was decided. */
  side?: Role;
  /**
   * Re-send on no ack / ok:false. Only for absolute values (verified, analog, owned): writing the
   * same value again is harmless. Pulses, counters and delta steps are never re-sent, because a
   * re-send is another press or click; they fail on the first no ack / ok:false.
   */
  retry: boolean;
}

export interface PendingHooks {
  maxRetries: number;
  timeoutFor(entry: PendingEntry): number;
  resend(entry: PendingEntry): void;
  ok(entry: PendingEntry, ack: AckBody): void;
  failed(entry: PendingEntry, reason: 'no_ack' | 'refused'): void;
  /** Called whenever an entry is added or removed (snapshot comparisons wait for quiet groups). */
  changed(entry: PendingEntry): void;
}

/**
 * Commands and corrections waiting for an ack. Timeout = settle_ms + margin + RTT. No ack
 * or ok:false → re-send (max maxRetries, absolute values only), then failed(). While the
 * receiver is lost nothing is re-sent and no retries are used up; resume continues.
 */
export class PendingAcks {
  private readonly entries = new Map<string, PendingEntry>();

  constructor(private readonly hooks: PendingHooks) {}

  start(entry: Omit<PendingEntry, 'attempt' | 'lastAck' | 'timer' | 'paused'>, receiverConnected: boolean): void {
    const full: PendingEntry = { ...entry, attempt: 0, lastAck: null, timer: null, paused: !receiverConnected };
    this.entries.set(full.id, full);
    if (receiverConnected) this.arm(full);
    this.hooks.changed(full);
  }

  get(id: string): PendingEntry | undefined {
    return this.entries.get(id);
  }

  /** Handles an ack from `from`. Returns false if nothing was waiting for it. */
  ack(from: string, ack: AckBody): boolean {
    const entry = this.entries.get(ack.id);
    if (!entry || entry.to !== from) return false;
    entry.lastAck = ack;
    // A var SimConnect refused to write (cannot_write) fails at once: a re-send cannot help (gate test 3 cause A).
    if (ack.ok) {
      this.remove(entry);
      this.hooks.ok(entry, ack);
    } else if (entry.retry && entry.attempt < this.hooks.maxRetries && ack.reason !== 'cannot_write') {
      entry.attempt++;
      this.hooks.resend(entry);
      this.arm(entry);
    } else {
      this.remove(entry);
      this.hooks.failed(entry, 'refused');
    }
    return true;
  }

  countInGroup(group: string): number {
    let n = 0;
    for (const e of this.entries.values()) if (e.group === group) n++;
    return n;
  }

  /** Receiver lost: stop timers, keep entries. */
  pauseFor(clientId: string): void {
    for (const e of this.entries.values()) {
      if (e.to !== clientId) continue;
      this.clearTimer(e);
      e.paused = true;
    }
  }

  /**
   * Receiver resumed. Entries newer than lastSeq were replayed already; older ones were
   * received before the outage but not acked, so they are re-sent (receivers dedupe by id).
   */
  resumeFor(clientId: string, lastSeq: number): void {
    for (const e of this.entries.values()) {
      if (e.to !== clientId || !e.paused) continue;
      e.paused = false;
      if (e.seq <= lastSeq) this.hooks.resend(e);
      this.arm(e);
    }
  }

  /** Drops entries without a result (superseded, receiver gone, full resync). Returns them. */
  cancelWhere(pred: (e: PendingEntry) => boolean): PendingEntry[] {
    const out: PendingEntry[] = [];
    for (const e of [...this.entries.values()]) {
      if (!pred(e)) continue;
      this.remove(e);
      out.push(e);
    }
    return out;
  }

  clear(): void {
    for (const e of this.entries.values()) this.clearTimer(e);
    this.entries.clear();
  }

  private arm(entry: PendingEntry): void {
    this.clearTimer(entry);
    // Never re-sent, but just as patient: waits as long as the re-sends of an absolute value would
    // (a burst of key presses is applied one by one on the other side).
    const wait = this.hooks.timeoutFor(entry) * (entry.retry ? 1 : this.hooks.maxRetries + 1);
    entry.timer = setTimeout(() => this.timeout(entry), wait);
  }

  private timeout(entry: PendingEntry): void {
    entry.timer = null;
    if (!this.entries.has(entry.id) || entry.paused) return;
    if (entry.retry && entry.attempt < this.hooks.maxRetries) {
      entry.attempt++;
      this.hooks.resend(entry);
      this.arm(entry);
    } else {
      this.remove(entry);
      this.hooks.failed(entry, entry.lastAck && !entry.lastAck.ok ? 'refused' : 'no_ack');
    }
  }

  private remove(entry: PendingEntry): void {
    this.clearTimer(entry);
    if (this.entries.delete(entry.id)) this.hooks.changed(entry);
  }

  private clearTimer(entry: PendingEntry): void {
    if (entry.timer) clearTimeout(entry.timer);
    entry.timer = null;
  }
}
