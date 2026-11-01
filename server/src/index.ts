import { resolve } from 'node:path';
import { buildServer } from './app.js';

const port = Number(process.env.CROSSCHECK_PORT ?? 8787);
const host = process.env.CROSSCHECK_HOST ?? '127.0.0.1';
const logDir = resolve(process.env.CROSSCHECK_LOG_DIR ?? 'logs');

const { app } = await buildServer({ logDir }, { logger: true });
await app.listen({ port, host });
console.log(`Crosscheck session server: ws://${host}:${port}/ws  (session logs: ${logDir})`);

const shutdown = () => {
  app.close().then(() => process.exit(0), () => process.exit(1));
};
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
