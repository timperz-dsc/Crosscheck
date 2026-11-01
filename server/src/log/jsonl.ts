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

const STDOUT_MAX = 600;

/** One compact JSON line per event. Join codes are not printed (they are the key to a session). */
function stdoutLine(session: string, t: number, line: Extract<LogLine, { dir: 'event' }>): string {
  const { dir: _dir, event, ...data } = line;
  delete data.join_code;
  const text = JSON.stringify({ time: new Date(t).toISOString(), session, event, ...data });
  return text.length <= STDOUT_MAX ? text : JSON.stringify({ time: new Date(t).toISOString(), session, event, truncated: text.slice(0, STDOUT_MAX) });
}

/**
 * One JSONL file per session: every non-stream message in both directions plus server
 * events, so a desync can be reported with the log attached. Written synchronously so the
 * file is complete even if the process dies.
 */
export class SessionLog {
  readonly path: string | null;
  private failed = false;

  /**
   * `echo`: also print every event line (not the messages) to stdout, one line each.
   * A log folder that cannot be written never stops the session: the failure is reported once.
   */
  constructor(dir: string | null, private readonly sessionId: string, private readonly echo = false, now = new Date()) {
    if (!dir) {
      this.path = null;
      return;
    }
    const stamp = now.toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
    try {
      mkdirSync(dir, { recursive: true });
      this.path = join(dir, `${stamp}-${sessionId}.jsonl`);
    } catch (err) {
      console.error(`session log folder ${dir} is not writable, no log file for session ${sessionId}: ${(err as Error).message}`);
      this.path = null;
    }
  }

  write(line: LogLine): void {
    const t = Date.now();
    if (this.echo && line.dir === 'event') console.log(stdoutLine(this.sessionId, t, line));
    if (!this.path || this.failed) return;
    const record = 'msg' in line && line.msg ? { ...line, msg: redact(line.msg as Envelope) } : line;
    try {
      appendFileSync(this.path, JSON.stringify({ t, time: new Date(t).toISOString(), ...record }) + '\n');
    } catch (err) {
      this.failed = true;
      console.error(`session log ${this.path} cannot be written: ${(err as Error).message}`);
    }
  }

  event(event: string, data: Record<string, unknown> = {}): void {
    this.write({ dir: 'event', event, ...data });
  }
}
