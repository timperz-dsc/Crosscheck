/** All timings and limits of the server. Defaults are the protocol v1 values. */
export interface ServerConfig {
  /** Ping interval in both directions. */
  heartbeatMs: number;
  /** A client is lost after this long without a message or without a pong. */
  lostAfterMs: number;
  /** A lost client's slot is kept this long for resume. */
  sessionTtlMs: number;
  /** Added to settle_ms (plus the receiver's RTT) before a command is re-sent. */
  ackMarginMs: number;
  /** Re-sends after the first send before a DesyncEvent. */
  maxRetries: number;
  /** Two absolute commands on the same var from different pilots within this window → "overridden" notice. */
  conflictWindowMs: number;
  /** How often clients send snapshot hashes (told to clients). */
  snapshotIntervalMs: number;
  /** Comparable hash mismatches in a row before values are requested. */
  mismatchRounds: number;
  /** A snapshot request is dropped if not answered in this time. */
  snapshotRequestTimeoutMs: number;
  /** Stream freeze during a control handover. */
  freezeMs: number;
  /** Both clients must send control_ack within this time. */
  controlAckTimeoutMs: number;
  /** settle_ms used for corrections. */
  correctionSettleMs: number;
  /** Corrections per var allowed within correctionLoopWindowMs before the var is held. */
  correctionLoopMax: number;
  correctionLoopWindowMs: number;
  /** Entries in the per-session replay log (ring buffer). */
  logSize: number;
  /** Max stream frames per second from the PF (extra frames are dropped). */
  streamMaxHz: number;
  /** Minimum time between two stream_dropped notices to the same client. */
  streamNoticeIntervalMs: number;
  /** No stream frame for this long → "stream stopped" in the log. */
  streamIdleMs: number;
  /**
   * Latest wins on a slow link (CRO-13): a stream frame is skipped for a receiver whose socket still has more
   * than this many bytes waiting to go out. The next frame replaces it; nothing is ever queued up.
   */
  streamBackpressureBytes: number;
  /** How often RTT and stream counts are written to the log. */
  summaryIntervalMs: number;
  /** Time a fresh socket has to send create/join/resume. */
  handshakeTimeoutMs: number;
  /** Directory for JSONL session logs. null = no files. */
  logDir: string | null;
  /** Print one line per session event to stdout (hosted: shows in the platform's log view). */
  echoEvents: boolean;
  /** Per IP: create requests allowed per createWindowMs. 0 = no limit. */
  createLimit: number;
  createWindowMs: number;
  /** Per IP: join and preflight_check requests allowed per joinWindowMs (also slows guessing codes). 0 = no limit. */
  joinLimit: number;
  joinWindowMs: number;
  /** Take the client IP from X-Forwarded-For (only behind a trusted proxy such as Railway's). */
  trustProxy: boolean;
  /** An indicator (observe var) must differ this long before it is raised as a quiet desync. */
  indicatorHoldMs: number;
  /**
   * An edge button held on one side only, or a toggle lamp that differs, is raised (red) after it
   * differed in 2 consecutive snapshot comparisons or for this long, whichever comes first.
   */
  buttonHoldMs: number;
  /** An edge button held down on one side only this long is "stuck pressed" (red, "Release it" in that cockpit). */
  stuckMs: number;
  /** An encoder display that still differs this long after the last click is one red line (never corrected). */
  encoderHoldMs: number;
  /** A push button lamp is compared once per burst; each edge extends the wait by this (plus margins). */
  effectSettleMs: number;
  /** Owned drift that changed by less than its step is sent at most this often. */
  driftMinIntervalMs: number;
  /** This many overrides on one var within fightWindowMs → "hardware fighting" advisory. */
  fightCount: number;
  fightWindowMs: number;
  /** A "not synced yet" line ends this long after the control last moved (CRO-34). */
  notSyncedExpireMs: number;
  /** Validate every outgoing message against the schema (tests). */
  validateOutgoing: boolean;
  /** Max WebSocket message size in bytes. */
  maxPayload: number;
}

export const DEFAULT_CONFIG: ServerConfig = {
  heartbeatMs: 1000,
  lostAfterMs: 5000,
  sessionTtlMs: 5 * 60 * 1000,
  ackMarginMs: 300,
  maxRetries: 3,
  conflictWindowMs: 1000,
  snapshotIntervalMs: 2000,
  mismatchRounds: 2,
  snapshotRequestTimeoutMs: 5000,
  freezeMs: 300,
  controlAckTimeoutMs: 2000,
  correctionSettleMs: 500,
  correctionLoopMax: 3,
  correctionLoopWindowMs: 60 * 1000,
  logSize: 2000,
  streamMaxHz: 30,
  streamNoticeIntervalMs: 5000,
  streamIdleMs: 1000,
  streamBackpressureBytes: 64 * 1024,
  summaryIntervalMs: 10 * 1000,
  handshakeTimeoutMs: 10 * 1000,
  logDir: null,
  echoEvents: false,
  indicatorHoldMs: 10 * 1000,
  buttonHoldMs: 2 * 1000,
  stuckMs: 5 * 1000,
  encoderHoldMs: 2 * 1000,
  effectSettleMs: 400,
  driftMinIntervalMs: 10 * 1000,
  fightCount: 3,
  fightWindowMs: 30 * 1000,
  notSyncedExpireMs: 2 * 60 * 1000,
  createLimit: 10,
  createWindowMs: 10 * 60 * 1000,
  joinLimit: 30,
  joinWindowMs: 10 * 60 * 1000,
  trustProxy: false,
  validateOutgoing: false,
  maxPayload: 256 * 1024,
};

const TIME_KEYS = [
  'heartbeatMs', 'lostAfterMs', 'sessionTtlMs', 'ackMarginMs', 'conflictWindowMs',
  'snapshotIntervalMs', 'snapshotRequestTimeoutMs', 'freezeMs', 'controlAckTimeoutMs',
  'correctionSettleMs', 'correctionLoopWindowMs', 'streamNoticeIntervalMs', 'streamIdleMs',
  'summaryIntervalMs', 'handshakeTimeoutMs', 'createWindowMs', 'joinWindowMs',
  'indicatorHoldMs', 'buttonHoldMs', 'stuckMs', 'encoderHoldMs', 'effectSettleMs', 'driftMinIntervalMs', 'fightWindowMs',
  'notSyncedExpireMs',
] as const satisfies readonly (keyof ServerConfig)[];

/**
 * Multiplies every timing by `scale` (tests run the real logic at e.g. 0.05 = 20× faster).
 * Never below 1 ms.
 */
export function scaleTimings(config: ServerConfig, scale: number): ServerConfig {
  const out = { ...config };
  for (const key of TIME_KEYS) out[key] = Math.max(1, Math.round(config[key] * scale));
  return out;
}
