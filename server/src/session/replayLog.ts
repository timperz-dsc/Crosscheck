import type { Envelope } from '../protocol/types.js';

interface Entry {
  seq: number;
  deliveries: Map<string, Envelope>;
}

/**
 * Per-session ring buffer of logged server-to-client messages (commands, acks, notices, ...),
 * so a client that resumes can get what it missed. When an entry for a client falls out of
 * the buffer, a resume from before that point needs a full snapshot resync instead.
 */
export class ReplayLog {
  private readonly entries: Entry[] = [];
  /** Highest seq per client that was evicted before it could be replayed. */
  private readonly evicted = new Map<string, number>();

  constructor(private readonly capacity: number) {}

  add(seq: number, clientId: string, message: Envelope): void {
    const last = this.entries.at(-1);
    if (last && last.seq === seq) {
      last.deliveries.set(clientId, message);
    } else {
      this.entries.push({ seq, deliveries: new Map([[clientId, message]]) });
      while (this.entries.length > this.capacity) {
        const old = this.entries.shift()!;
        for (const id of old.deliveries.keys()) this.evicted.set(id, Math.max(this.evicted.get(id) ?? 0, old.seq));
      }
    }
  }

  /** Messages for this client after lastSeq, oldest first; null if some were already evicted. */
  replayFor(clientId: string, lastSeq: number): Envelope[] | null {
    if ((this.evicted.get(clientId) ?? 0) > lastSeq) return null;
    const out: Envelope[] = [];
    for (const entry of this.entries) {
      if (entry.seq <= lastSeq) continue;
      const msg = entry.deliveries.get(clientId);
      if (msg) out.push(msg);
    }
    return out;
  }

  forget(clientId: string): void {
    this.evicted.delete(clientId);
    for (const entry of this.entries) entry.deliveries.delete(clientId);
  }

  get size(): number {
    return this.entries.length;
  }
}
