import type { WebSocket } from 'ws';
import type { ServerConfig } from '../config.js';
import type { ProtocolValidator } from '../protocol/validate.js';
import type { CreateBody, Envelope, ErrorCode, JoinBody, ResumeBody } from '../protocol/types.js';
import { generateJoinCode, newSessionId, normalizeJoinCode } from './joinCode.js';
import { Session, type Conn } from './session.js';
import { comparePreflight } from './preflight.js';

class SocketConn implements Conn {
  slot?: Conn['slot'];
  session?: Session;
  handshakeTimer: NodeJS.Timeout | null = null;

  constructor(private readonly ws: WebSocket) {}

  send(text: string): void {
    if (this.ws.readyState === this.ws.OPEN) this.ws.send(text);
  }

  terminate(): void {
    this.ws.terminate();
  }

  isOpen(): boolean {
    return this.ws.readyState === this.ws.OPEN;
  }
}

export class SessionManager {
  readonly sessions = new Map<string, Session>();
  private readonly byCode = new Map<string, Session>();
  private readonly sockets = new Set<WebSocket>();

  constructor(private readonly config: ServerConfig, private readonly validator: ProtocolValidator) {}

  handleSocket(ws: WebSocket): void {
    const conn = new SocketConn(ws);
    this.sockets.add(ws);
    conn.handshakeTimer = setTimeout(() => {
      if (!conn.session) ws.terminate();
    }, this.config.handshakeTimeoutMs);
    ws.on('message', (data) => {
      try {
        this.onMessage(conn, data.toString());
      } catch (err) {
        // A bug in one message must not stop the server (both sims keep flying either way).
        if (this.config.validateOutgoing) throw err;
        conn.session?.log.event('internal_error', { client: conn.slot?.id ?? null, error: String((err as Error).stack ?? err) });
        console.error(err);
      }
    });
    ws.on('close', () => {
      this.sockets.delete(ws);
      if (conn.handshakeTimer) clearTimeout(conn.handshakeTimer);
      if (conn.session && conn.slot) conn.session.connectionClosed(conn.slot, conn);
    });
    ws.on('error', () => { /* close follows */ });
  }

  private onMessage(conn: SocketConn, raw: string): void {
    let data: unknown;
    try {
      data = JSON.parse(raw);
    } catch {
      return this.error(conn, 'invalid_json', 'message is not valid JSON');
    }
    const result = this.validator.validate(data);
    if (!result.ok) {
      conn.session?.log.event('invalid_message', { client: conn.slot?.id ?? null, code: result.code, details: result.details, raw: raw.slice(0, 2000) });
      return this.error(conn, result.code, result.message, result.details);
    }
    const msg = result.message;
    if (conn.session && conn.slot) {
      if (msg.type === 'create' || msg.type === 'join' || msg.type === 'resume') {
        return this.error(conn, 'forbidden', `already in session ${conn.session.id}`);
      }
      return conn.session.handle(conn.slot, msg);
    }
    switch (msg.type) {
      case 'create': return this.create(conn, msg);
      case 'join': return this.join(conn, msg);
      case 'preflight_check': return this.check(conn, msg);
      case 'resume': return this.resume(conn, msg);
      default: return this.error(conn, 'not_in_session', `${msg.type} needs a session: send create, join or resume first`);
    }
  }

  private attached(conn: SocketConn): void {
    if (conn.handshakeTimer) clearTimeout(conn.handshakeTimer);
    conn.handshakeTimer = null;
  }

  private create(conn: SocketConn, msg: Envelope): void {
    const body = msg.body as unknown as CreateBody;
    let code = generateJoinCode();
    while (this.byCode.has(code)) code = generateJoinCode();
    const session = new Session(newSessionId(), code, body.preflight, this.config, this.validator, (s) => this.ended(s));
    this.sessions.set(session.id, session);
    this.byCode.set(code, session);
    this.attached(conn);
    session.log.write({ dir: 'in', client: null, msg });
    session.addHost(conn, body.preflight, body.name);
  }

  private join(conn: SocketConn, msg: Envelope): void {
    const body = msg.body as unknown as JoinBody;
    const code = normalizeJoinCode(body.join_code);
    const session = code ? this.byCode.get(code) : undefined;
    if (!session) {
      return this.send(conn, null, 'join_rejected', { reason: 'unknown_code', message: 'No session with this join code.', diff: [] });
    }
    session.log.write({ dir: 'in', client: null, msg });
    const result = session.join(conn, body.preflight, body.name);
    if (!result.ok) {
      const rejected = { reason: result.reason, message: result.message, diff: result.diff };
      session.log.write({ dir: 'out', to: [], msg: { v: 1, type: 'join_rejected', session: null, ts: Date.now(), body: rejected } });
      return this.send(conn, null, 'join_rejected', rejected);
    }
    this.attached(conn);
  }

  /** Preflight comparison before joining: takes no slot and changes nothing. */
  private check(conn: SocketConn, msg: Envelope): void {
    const body = msg.body as unknown as JoinBody;
    const code = normalizeJoinCode(body.join_code);
    const session = code ? this.byCode.get(code) : undefined;
    if (!session) {
      return this.send(conn, null, 'join_rejected', { reason: 'unknown_code', message: 'No session with this join code.', diff: [] });
    }
    if (session.slots.length >= 2) {
      return this.send(conn, null, 'join_rejected', { reason: 'session_full', message: 'This session already has two pilots.', diff: [] });
    }
    const { rejects, warnings } = comparePreflight(session.reference, body.preflight);
    session.log.event('preflight_checked', { rejects: rejects.map((r) => r.field), warnings: warnings.map((w) => w.field) });
    this.send(conn, null, 'preflight_result', { join_code: session.joinCode, reference: session.reference, rejects, warnings });
  }

  private resume(conn: SocketConn, msg: Envelope): void {
    const body = msg.body as unknown as ResumeBody;
    const session = msg.session ? this.sessions.get(msg.session) : undefined;
    const slot = session?.slot(body.client_id);
    if (!session || !slot || !session.checkToken(slot, body.resume_token)) {
      session?.log.event('resume_failed', { client: body.client_id });
      return this.error(conn, 'resume_failed', 'Session or client not found (expired?) or wrong resume token. Join again.');
    }
    session.log.write({ dir: 'in', client: slot.id, msg });
    this.attached(conn);
    session.resume(slot, conn, body.last_seq);
  }

  private ended(session: Session): void {
    this.sessions.delete(session.id);
    if (this.byCode.get(session.joinCode) === session) this.byCode.delete(session.joinCode);
  }

  private error(conn: SocketConn, code: ErrorCode, message: string, details?: string[]): void {
    this.send(conn, conn.session?.id ?? null, 'error', { code, message, ...(details && details.length > 0 ? { details } : {}) });
  }

  private send(conn: SocketConn, session: string | null, type: string, body: object): void {
    conn.send(JSON.stringify({ v: 1, type, session, ts: Date.now(), body }));
  }

  /** Server shutdown. */
  closeAll(): void {
    for (const s of [...this.sessions.values()]) s.end('server_shutdown');
    for (const ws of this.sockets) ws.terminate();
    this.sockets.clear();
  }
}
