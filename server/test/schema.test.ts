import { readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { WebSocket } from 'ws';
import { SCHEMA_DIR, sharedValidator } from '../src/protocol/validate.js';
import { startServer } from './fake/harness.js';

const EXAMPLES = resolve(SCHEMA_DIR, '../examples');

describe('protocol schema (protocol/schema, shared with the C# bridge)', () => {
  const v = sharedValidator();

  it('has an example for every message type, and every example is valid', () => {
    const examples = readdirSync(EXAMPLES).filter((f) => f.endsWith('.json'));
    expect(examples.map((f) => f.replace('.json', '')).sort()).toEqual([...v.types].sort());
    for (const file of examples) {
      const msg = JSON.parse(readFileSync(join(EXAMPLES, file), 'utf8'));
      const result = v.validate(msg);
      expect(result.ok ? 'ok' : `${file}: ${result.message}`).toBe('ok');
      expect(v.validateAny(msg)).toBe(true);
    }
  });

  it('rejects malformed messages', () => {
    const base = { v: 4, session: 's_abcdefg', ts: 1 };
    const bad = [
      { ...base, type: 'command', body: { id: 'a', var: 'X', class: 'delta', value: 1, group: 'g', settle_ms: 10 } }, // delta with value
      { ...base, type: 'command', body: { id: 'a', var: 'X', class: 'verified', group: 'g', settle_ms: 10 } }, // no value
      { ...base, type: 'command', body: { id: 'a', var: 'X', class: 'observe', value: 1, group: 'g', settle_ms: 10 } }, // not commandable
      { ...base, type: 'ack', body: { id: 'a', ok: true, observed: 1, extra: 1 } }, // unknown field
      { ...base, type: 'ack', extra: 1, body: { id: 'a', ok: true, observed: 1 } }, // unknown envelope field
      { ...base, v: 1, type: 'ack', body: { id: 'a', ok: true, observed: 1 } }, // old protocol
      { ...base, v: 2, type: 'ack', body: { id: 'a', ok: true, observed: 1 } }, // protocol 2 (bridge 0.2.0)
      { ...base, v: 3, type: 'ack', body: { id: 'a', ok: true, observed: 1 } }, // protocol 3 (bridge 0.3.x)
      { ...base, type: 'nonsense', body: {} },
    ];
    for (const msg of bad) expect(v.validate(msg).ok).toBe(false);
    expect(v.validate({ ...base, v: 1, type: 'ack', body: {} })).toMatchObject({ ok: false, code: 'unsupported_version' });
  });

  it('the server answers invalid input with an error and keeps the socket open', async () => {
    const server = await startServer();
    try {
      const ws = new WebSocket(server.url);
      await new Promise((r) => ws.once('open', r));
      const replies: { type: string; body: { code: string } }[] = [];
      ws.on('message', (d) => replies.push(JSON.parse(d.toString())));
      ws.send('not json');
      ws.send(JSON.stringify({ v: 9, type: 'ping', session: null, ts: 1, body: {} }));
      ws.send(JSON.stringify({ v: 4, type: 'command', session: null, ts: 1, body: { id: 'a', var: 'X', class: 'verified', value: 1, group: 'g', settle_ms: 1 } }));
      await new Promise((r) => setTimeout(r, 100));
      expect(replies.map((r) => r.body.code)).toEqual(['invalid_json', 'unsupported_version', 'not_in_session']);
      expect(ws.readyState).toBe(WebSocket.OPEN);
      ws.terminate();
    } finally {
      await server.close();
    }
  });
});
