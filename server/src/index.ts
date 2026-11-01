import { resolve } from 'node:path';
import { buildServer } from './app.js';

// Hosted (Railway): PORT is set and the server must be reachable from outside → 0.0.0.0.
// Local development: 127.0.0.1:8787. CROSSCHECK_HOST / CROSSCHECK_PORT override either.
const hosted = process.env.PORT !== undefined;
const port = Number(process.env.PORT ?? process.env.CROSSCHECK_PORT ?? 8787);
const host = process.env.CROSSCHECK_HOST ?? (hosted ? '0.0.0.0' : '127.0.0.1');
const logDir = resolve(process.env.LOG_DIR ?? process.env.CROSSCHECK_LOG_DIR ?? 'logs');
// Railway's proxy puts the real client address into X-Forwarded-For (needed for the per-IP limit).
const trustProxy = process.env.CROSSCHECK_TRUST_PROXY !== undefined
  ? process.env.CROSSCHECK_TRUST_PROXY === '1'
  : process.env.RAILWAY_ENVIRONMENT_NAME !== undefined;

// Per-IP limits on create and join (0 = no limit). Tests that make many sessions from one address switch them off.
const limits: { createLimit?: number; joinLimit?: number; indicatorHoldMs?: number } = {};
if (process.env.CROSSCHECK_CREATE_LIMIT !== undefined) limits.createLimit = Number(process.env.CROSSCHECK_CREATE_LIMIT);
if (process.env.CROSSCHECK_JOIN_LIMIT !== undefined) limits.joinLimit = Number(process.env.CROSSCHECK_JOIN_LIMIT);
// How long an indicator must differ before it is shown (default 10 s; tests shorten it).
if (process.env.CROSSCHECK_INDICATOR_HOLD_MS !== undefined) limits.indicatorHoldMs = Number(process.env.CROSSCHECK_INDICATOR_HOLD_MS);

const { app } = await buildServer({ logDir, echoEvents: true, trustProxy, ...limits }, { logger: true });
await app.listen({ port, host });
console.log(`Crosscheck session server: ${host}:${port}/ws  (session logs: ${logDir}, trust proxy: ${trustProxy})`);

const shutdown = () => {
  app.close().then(() => process.exit(0), () => process.exit(1));
};
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
