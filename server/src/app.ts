import websocket from '@fastify/websocket';
import Fastify, { type FastifyInstance } from 'fastify';
import { DEFAULT_CONFIG, type ServerConfig } from './config.js';
import { PROTOCOL_VERSION } from './protocol/types.js';
import { sharedValidator } from './protocol/validate.js';
import { SessionManager } from './session/manager.js';

export interface CrosscheckServer {
  app: FastifyInstance;
  manager: SessionManager;
  config: ServerConfig;
}

/** Builds the HTTP + WebSocket server (not listening yet). WebSocket endpoint: /ws. */
export async function buildServer(overrides: Partial<ServerConfig> = {}, opts: { logger?: boolean } = {}): Promise<CrosscheckServer> {
  const config: ServerConfig = { ...DEFAULT_CONFIG, ...overrides };
  const manager = new SessionManager(config, sharedValidator());
  const app = Fastify({ logger: opts.logger ?? false });
  await app.register(websocket, { options: { maxPayload: config.maxPayload } });
  app.get('/health', async () => ({ ok: true, protocol: PROTOCOL_VERSION, sessions: manager.sessions.size }));
  await app.register(async (scope) => {
    scope.get('/ws', { websocket: true }, (socket) => manager.handleSocket(socket));
  });
  app.addHook('preClose', async () => manager.closeAll());
  return { app, manager, config };
}
