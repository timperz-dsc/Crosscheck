// Extracts the gate test 2 panel stream (CRO-34) from one bridge log into
// server/test/fixtures/gate-test-2.json (key panel_stream). Read-only on the log.
//
//   node tools/replay-extract/extract-panel-stream.mjs <bridge-log.jsonl>
//
// Anonymised: only relative times (ms), var names, reasons, tiers, values, advisory keys/labels and
// seats (host/guest). Session ids, client ids, names, paths and server messages are dropped; a client
// id becomes "self" (this bridge) or "peer". The stream is replayed as it was (protocol v3, bridge 0.3.2):
// lines the 0.4 engine no longer sends (encoder refused per click, lamp checks mid-burst) closed within
// a second in the log, so the panel's 2 s hold has to keep them off the panel by itself.

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const log = process.argv[2];
if (!log) {
  console.error('usage: node tools/replay-extract/extract-panel-stream.mjs <bridge-log.jsonl>');
  process.exit(2);
}
const repo = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const fixturePath = join(repo, 'server', 'test', 'fixtures', 'gate-test-2.json');

let self = null;
let seat = null;
let isPf = null;
let t0 = null;
const events = [];
const lastMove = new Map();
const own = new Map();
const CONTEXT = /^(B_FCU_|B_PED_RMP\d_POWER$|I_PED_RMP\d_)/;
const rel = (t) => t - t0;
const num = (v) => (typeof v === 'number' ? v : null);

for (const line of readFileSync(log, 'utf8').split('\n')) {
  if (!line) continue;
  let j;
  try { j = JSON.parse(line); } catch { continue; }
  const m = j.msg;
  if (m?.type === 'joined' || m?.type === 'created') {
    self ??= m.body.client_id;
    seat ??= m.body.seat;
    isPf ??= m.body.pf_client_id === m.body.client_id;
  }
  if (!self) continue;
  t0 ??= j.t;
  const t = rel(j.t);
  const who = (id) => (id == null ? null : id === self ? 'self' : 'peer');
  const move = (name) => {
    // One activity tick per var per 100 ms is enough for a 2 s hold.
    if (t - (lastMove.get(name) ?? -1e9) < 100) return;
    lastMove.set(name, t);
    events.push([t, 'm', name]);
  };

  if (j.dir === 'in' && m?.type === 'desync') {
    const b = m.body;
    events.push([t, 'd', b.var, b.reason, b.tier, num(b.expected), num(b.observed), num(b.values?.pf), num(b.values?.pm), who(b.client_id), b.group ?? null]);
  } else if (j.dir === 'in' && m?.type === 'notice' && m.body.kind === 'desync_cleared') {
    events.push([t, 'c', m.body.var, m.body.reason]);
  } else if (j.dir === 'in' && m?.type === 'advisory') {
    const b = m.body;
    events.push([t, 'a', b.key, b.kind, b.active ? 1 : 0, b.var ?? null, b.label ?? null, b.seat, b.count ?? null]);
  } else if (j.dir === 'out' && m?.type === 'snapshot_values') {
    // This cockpit's power flags and mode lamps (FCU, RMP): the replay compares encoders and powered
    // indicators only while powered and in the same mode, as the 0.4 server does (compare_when).
    for (const v of m.body.values) {
      if (!CONTEXT.test(v.var) || own.get(v.var) === v.value) continue;
      own.set(v.var, v.value);
      events.push([t, 'v', v.var, v.value]);
    }
  } else if (j.dir === 'in' && m?.type === 'snapshot_diff') {
    // The other cockpit's power flags and mode lamps, where they differ from this one.
    for (const d of m.body.differences) {
      if (CONTEXT.test(d.var)) events.push([t, 'x', d.var, num(d.pf), num(d.pm)]);
    }
  } else if (j.dir === 'out' && m?.type === 'command') {
    move(m.body.var);
  } else if (j.dir === 'event' && j.event === 'applied' && !j.correction) {
    move(j.var);
  }
}

const text = JSON.stringify(events);
for (const bad of [/"s_[A-Za-z0-9_-]{6,}"/, /"c_[A-Za-z0-9_-]{6,}/, /[A-Z]:\\\\/, /@/]) {
  if (bad.test(text)) throw new Error(`not anonymous: ${bad}`);
}

const fixture = JSON.parse(readFileSync(fixturePath, 'utf8'));
fixture.panel_stream = {
  note: 'CRO-34: what the guest bridge received in gate test 2 (89 min), for the panel replay. Events: '
    + '[t, "d", var, reason, tier, expected, observed, pf, pm, who, group] desync opened; [t, "c", var, reason] desync cleared; '
    + '[t, "a", key, kind, active, var, label, seat, count] advisory; [t, "m", var] a pilot moved or pressed it (sent or applied); '
    + '[t, "v", var, value] this cockpit\'s FCU/RMP power flag or mode lamp changed; [t, "x", var, pf, pm] a snapshot diff showed it differ. '
    + 'who: the client_id of the desync, self = this bridge. Replayed as it was (protocol v3, bridge 0.3.2).',
  seat,
  is_pf: isPf,
  // The moment of the 19 red cards (20:08:20 host time; this log runs about 10 s ahead).
  peak_ms: null,
  end_ms: events.at(-1)[0],
  events,
};
// Peak: the most open red desyncs at once, as the old panel showed them.
{
  const open = new Map();
  let best = 0;
  for (const e of events) {
    if (e[1] === 'd' && e[4] === 'red') open.set(e[2], true);
    if (e[1] === 'c') open.delete(e[2]);
    if (open.size > best) { best = open.size; fixture.panel_stream.peak_ms = e[0]; fixture.panel_stream.peak_old_red = best; }
  }
}
// One event per line keeps the file small and readable in a diff.
const pretty = JSON.stringify({ ...fixture, panel_stream: { ...fixture.panel_stream, events: '@EVENTS@' } }, null, 2);
const lines = events.map((e) => '      ' + JSON.stringify(e)).join(',\n');
writeFileSync(fixturePath, pretty.replace('"@EVENTS@"', `[\n${lines}\n    ]`) + '\n');
console.log(`${events.length} events, ${(text.length / 1024).toFixed(0)} KiB, peak ${fixture.panel_stream.peak_old_red} red at ${fixture.panel_stream.peak_ms} ms`);
