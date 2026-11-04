import { afterEach, describe, expect, it } from 'vitest';
import { body, sleep, startPair, waitUntil, type Pair } from './fake/harness.js';

let pair: Pair;
afterEach(async () => pair?.close());

const frame = (n: number) => ({ lat: 47.46 + n * 1e-5, lon: 8.55, alt_ft: 1416, hdg_deg: 280 });

describe('13. stream (physics from the PF)', () => {
  it('relays the PF stream without seq and without logging each frame', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    for (let i = 0; i < 5; i++) host.stream(i, frame(i));
    await waitUntil(() => guest.messages('stream').length === 5, '5 frames relayed');

    const frames = guest.messages('stream');
    expect(frames.map((m) => body(m).frame)).toEqual([0, 1, 2, 3, 4]);
    expect(frames.every((m) => m.seq === undefined && body(m).from === host.clientId)).toBe(true);
    const lines = server.logLines();
    expect(lines.some((l) => (l.msg as { type?: string } | undefined)?.type === 'stream')).toBe(false);
    expect(lines.some((l) => l.event === 'stream_started')).toBe(true);
  });

  it('drops stream from the pilot monitoring and tells it (once)', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    for (let i = 0; i < 3; i++) guest.stream(i, frame(i));
    await guest.waitFor('notice', (m) => body(m).kind === 'stream_dropped');
    await sleep(50);
    expect(guest.messages('notice', (m) => body(m).kind === 'stream_dropped')).toHaveLength(1);
    expect(host.messages('stream')).toHaveLength(0);
  });

  it('drops a frame that is not newer than the last one relayed (latest wins, never replayed)', async () => {
    pair = await startPair();
    const { host, guest } = pair;
    for (const n of [5, 3, 5, 6, 4, 7]) host.stream(n, frame(n));
    await waitUntil(() => guest.messages('stream').some((m) => body(m).frame === 7), 'frame 7 relayed');
    await sleep(30);
    expect(guest.messages('stream').map((m) => body(m).frame)).toEqual([5, 6, 7]);
  });

  it('skips a frame for a receiver whose socket is still busy, and counts it', async () => {
    // -1: every socket counts as busy, so every frame is skipped (the next frame would replace it).
    pair = await startPair({ server: { streamBackpressureBytes: -1, summaryIntervalMs: 20 } });
    const { host, guest, server } = pair;
    for (let i = 0; i < 4; i++) host.stream(i, frame(i));
    await waitUntil(() => server.logLines().some((l) => l.event === 'summary'
      && ((l as { clients?: { stream_skipped_slow?: number }[] }).clients ?? []).some((c) => c.stream_skipped_slow === 4)), 'skips counted');
    expect(guest.messages('stream')).toHaveLength(0);
  });

  it('drops frames above the rate limit', async () => {
    pair = await startPair();
    const { host, guest, server } = pair;
    for (let i = 0; i < 80; i++) host.stream(i, frame(i));
    await sleep(100);
    expect(guest.messages('stream').length).toBe(Math.ceil(server.config.streamMaxHz * 1.2));
  });

  it('freezes stream during a handover; afterwards only the new PF streams', async () => {
    pair = await startPair({ server: { freezeMs: 200 } });
    const { host, guest } = pair;
    await sleep(pair.server.config.snapshotIntervalMs * 2);

    guest.requestControl();
    await host.waitFor('control_handover');
    host.stream(100, frame(100));
    guest.stream(200, frame(200));
    await host.waitFor('control_changed');
    await sleep(20);
    expect(guest.messages('stream')).toHaveLength(0);
    expect(host.messages('stream')).toHaveLength(0);

    guest.stream(201, frame(201));
    host.stream(101, frame(101));
    await host.waitFor('stream', (m) => body(m).frame === 201);
    await host.waitFor('notice', (m) => body(m).kind === 'stream_dropped');
    await sleep(20);
    expect(guest.messages('stream')).toHaveLength(0);
  });
});
