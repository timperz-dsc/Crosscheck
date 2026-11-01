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
  /** How often RTT and stream counts are written to the log. */
  summaryIntervalMs: number;
  /** Time a fresh socket has to send create/join/resume. */
  handshakeTimeoutMs: number;
  /** Directory for JSONL session logs. null = no files. */
  logDir: string | null;
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
  summaryIntervalMs: 10 * 1000,
  handshakeTimeoutMs: 10 * 1000,
  logDir: null,
  validateOutgoing: false,
  maxPayload: 256 * 1024,
};

const TIME_KEYS = [
  'heartbeatMs', 'lostAfterMs', 'sessionTtlMs', 'ackMarginMs', 'conflictWindowMs',
  'snapshotIntervalMs', 'snapshotRequestTimeoutMs', 'freezeMs', 'controlAckTimeoutMs',
  'correctionSettleMs', 'correctionLoopWindowMs', 'streamNoticeIntervalMs', 'streamIdleMs',
  'summaryIntervalMs', 'handshakeTimeoutMs',
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
