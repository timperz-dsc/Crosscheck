import { appendFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import type { Envelope } from '../protocol/types.js';

export type LogLine =
  | { dir: 'in'; client: string | null; msg: Envelope }
  | { dir: 'out'; to: string[]; msg: Envelope }
  | { dir: 'event'; event: string; [key: string]: unknown };

const SECRET_KEYS = new Set(['resume_token']);

function redact(msg: Envelope): Envelope {
  const body = msg.body as Record<string, unknown>;
  if (!Object.keys(body).some((k) => SECRET_KEYS.has(k))) return msg;
  const copy: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(body)) copy[k] = SECRET_KEYS.has(k) ? '<redacted>' : v;
  return { ...msg, body: copy };
}

/**
 * One JSONL file per session: every non-stream message in both directions plus server
 * events, so a desync can be reported with the log attached. Written synchronously so the
 * file is complete even if the process dies.
 */
export class SessionLog {
  readonly path: string | null;
  private failed = false;

  constructor(dir: string | null, sessionId: string, now = new Date()) {
    if (!dir) {
      this.path = null;
      return;
    }
    mkdirSync(dir, { recursive: true });
    const stamp = now.toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
    this.path = join(dir, `${stamp}-${sessionId}.jsonl`);
  }

  write(line: LogLine): void {
    if (!this.path) return;
    const record = 'msg' in line && line.msg ? { ...line, msg: redact(line.msg as Envelope) } : line;
    const t = Date.now();
    appendFileSync(this.path, JSON.stringify({ t, time: new Date(t).toISOString(), ...record }) + '\n');
  }

  event(event: string, data: Record<string, unknown> = {}): void {
    this.write({ dir: 'event', event, ...data });
  }
}
