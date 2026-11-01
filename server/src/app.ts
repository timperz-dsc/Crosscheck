import websocket from '@fastify/websocket';
import { readFileSync } from 'node:fs';
import Fastify, { type FastifyInstance } from 'fastify';
import { DEFAULT_CONFIG, type ServerConfig } from './config.js';
import { PROTOCOL_VERSION } from './protocol/types.js';
import { sharedValidator } from './protocol/validate.js';
import { SessionManager } from './session/manager.js';

/** From server/package.json (one level above src/). */
export const SERVER_VERSION: string = (JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')) as { version: string }).version;

export interface CrosscheckServer {
  app: FastifyInstance;
  manager: SessionManager;
  config: ServerConfig;
}

/** Builds the HTTP + WebSocket server (not listening yet). WebSocket endpoint: /ws. */
export async function buildServer(overrides: Partial<ServerConfig> = {}, opts: { logger?: boolean } = {}): Promise<CrosscheckServer> {
  const config: ServerConfig = { ...DEFAULT_CONFIG, ...overrides };
  const manager = new SessionManager(config, sharedValidator());
  const app = Fastify({ logger: opts.logger ?? false, trustProxy: config.trustProxy });
  await app.register(websocket, { options: { maxPayload: config.maxPayload } });
  app.get('/health', async () => ({ ok: true, version: SERVER_VERSION, protocol: PROTOCOL_VERSION, sessions: manager.sessions.size }));
  await app.register(async (scope) => {
    scope.get('/ws', { websocket: true }, (socket, req) => manager.handleSocket(socket, req.ip));
  });
  app.addHook('preClose', async () => manager.closeAll());
  return { app, manager, config };
}
