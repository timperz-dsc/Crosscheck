import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertIcon, Button, Mark, PfLostLine, TakeoverLeversLine } from '../components/parts';
import { pickHeadline } from '../panel';
import type { Actions, ControlNotice, Panel, PanelItem, PanelLine, Status } from '../types';
import {
  SideName, actionLabel, amberText, driftText, driftWarningText, handoverDetail, handoverLine, indicatorsLine, itemValues, lastCompared,
  leversText, lineText, moreText, notSyncedDetail, notSyncedLine, otherSide, outsideLine, peerLostText, roleName, sideName, timeOfDay,
} from '../words';

interface Props {
  status: Status;
  actions: Actions;
  busy: boolean;
  error: string | null;
  confirmPeerLost: boolean;
  /** Leave asked while this aircraft is held frozen in the air: the bridge's question, or null. */
  confirmLeaveInAir?: string | null;
  cancelLeave?: () => void;
  /** Current time in ms (ticks every second). */
  now: number;
}

const EMPTY: Panel = {
  red: [], not_synced: { count: 0, items: [] }, outside: { count: 0, items: [] }, amber: [], indicators: { count: 0, items: [] }, log: [],
};

/**
 * The session panel (CRO-34, laid out as a desktop window in CRO-39), for a quick glance in flight.
 * Left pane: the headline line (the most important red line and its action), below it the full red
 * list on demand and everything else collapsed, and the crew with the control button at the bottom.
 * Right pane: the Panels summary and the log. Only the lists scroll, each inside its own pane.
 */
export function Session({ status, actions, busy, error, confirmPeerLost, confirmLeaveInAir = null, cancelLeave = () => {}, now }: Props) {
  const panel = status.panel ?? EMPTY;
  const crew = status.crew ?? [];
  const me = crew.find((c) => c.you);
  const peer = crew.find((c) => !c.you);
  const peerWord = sideName(peer?.side ?? otherSide(status));
  const isPf = status.is_pf ?? status.role === 'pf';
  const notice = useNotice(status.control_notice ?? null);
  const red = useRedLines(panel.red);
  // When "I have control" was clicked while the thrust levers did not match (ms), or null.
  const [askLeversAt, setAskLeversAt] = useState<number | null>(null);
  const askLevers = askLeversAt !== null;
  const levers = !isPf && status.physics && status.physics.levers.length > 0 ? leversText(status.physics, peer?.side ?? otherSide(status)) : null;
  const takeControl = () => {
    // "Match your thrust levers" first (CRO-14): checked against this pilot's hardware levers (0.6.2).
    if (levers && !askLevers) setAskLeversAt(now);
    else {
      setAskLeversAt(null);
      void actions.takeControl(false);
    }
  };
  // Owner decision 2026-11-05: once the levers match, control is taken, but only within 30 s of the click;
  // after that the request expires and nothing happens.
  useEffect(() => {
    if (askLeversAt === null) return;
    if (isPf || now - askLeversAt > LEVER_ASK_MS) setAskLeversAt(null);
    else if (!levers) {
      setAskLeversAt(null);
      void actions.takeControl(false);
    }
  }, [askLeversAt, levers, isPf, now, actions]);

  return (
    <div className="session">
      <div className="pane pane-main">
        <PfLostLine status={status} actions={actions} busy={busy} />
        {isPf && <TakeoverLeversLine status={status} />}
        <Headline red={red} peerWord={peerWord} actions={actions} busy={busy} compared={lastCompared(status.last_compared_at, now)} />

        <div className="pane-scroll">
          {red.open && red.others.length > 0 && (
            <div id="red-lines">
              {red.others.map((l) => <ControlLine key={l.control} line={l} peerWord={peerWord} actions={actions} busy={busy} />)}
            </div>
          )}
          {confirmLeaveInAir && (
            <section className="note" role="alert">
              <span className="note-row">
                <span className="differs">{confirmLeaveInAir}</span>
                <Button small onClick={() => void actions.leave(true)} disabled={busy}>Release and leave</Button>
                <Button small variant="secondary" onClick={cancelLeave}>Stay</Button>
              </span>
            </section>
          )}
          {notice.visible && notice.data && <ControlChange notice={notice.data} peerWord={peerWord} onDismiss={notice.dismiss} />}
          <CollapsedLines status={status} panel={panel} peerWord={peerWord} isPf={isPf} confirmPeerLost={confirmPeerLost} actions={actions} busy={busy} now={now} />
        </div>

        <div className="pane-foot">
          <dl className="crew">
            {[...crew].sort((a, b) => (a.side === 'captain' ? 0 : 1) - (b.side === 'captain' ? 0 : 1)).map((c) => (
              <div key={c.side}>
                <dt>{SideName(c.side)}{c.you ? ' (you)' : ''}</dt>
                <dd className="muted">{roleName(c.role)}{c.state === 'lost' ? ', lost connection' : ''}</dd>
              </div>
            ))}
          </dl>
          {me && peer && (
            <div className="row">
              {isPf ? (
                <>
                  <span className="hint">Gives control to the {peerWord} at once.</span>
                  <Button onClick={() => void actions.giveControl()} disabled={busy || peer.state !== 'connected'}>You have control</Button>
                </>
              ) : (
                <>
                  {askLevers && levers ? (
                    <span className="warning">{levers}</span>
                  ) : (
                    <span className="hint">Takes over at once. The takeover button on your sidestick does the same.</span>
                  )}
                  <Button onClick={takeControl} disabled={busy}>{askLevers && levers ? 'Take control anyway' : 'I have control'}</Button>
                  {askLevers && levers && <Button variant="secondary" onClick={() => setAskLeversAt(null)}>Cancel</Button>}
                </>
              )}
            </div>
          )}
          {error && <p className="warning" style={{ flexBasis: '100%' }}>{error}</p>}
        </div>
      </div>

      <aside className="pane pane-side">
        <section className="side-section side-panels" aria-labelledby="panels-title">
          <h2 id="panels-title">Panels</h2>
          <dl className="panels">
            {(status.groups ?? []).map((g) => (
              <div key={g.id} style={{ display: 'contents' }}>
                <dt className={g.differs > 0 ? 'strong' : undefined}>{g.label}</dt>
                <dd className={g.differs > 0 ? 'differs' : 'muted'}>{g.differs > 0 ? `${g.differs} ${g.differs === 1 ? 'differs' : 'differ'}` : 'Match'}</dd>
              </div>
            ))}
            {(status.owned ?? []).map((o) => (
              <div key={o.var} style={{ display: 'contents' }}>
                <dt>{o.label}</dt>
                <dd className="muted">{driftText(o.delta, o.unit)}</dd>
              </div>
            ))}
          </dl>
        </section>
        <section className="side-section side-log" aria-labelledby="log-title">
          <h2 id="log-title">Log</h2>
          {panel.log.length === 0 ? (
            <p className="muted">Nothing yet. Every change either of you makes appears here.</p>
          ) : (
            <ol className="recent log" aria-label="Log">
              {panel.log.map((r, i) => (
                <li key={`${r.at}-${i}`}>
                  <time dateTime={r.at}>{timeOfDay(r.at)}</time>
                  <span>{r.text}</span>
                </li>
              ))}
            </ol>
          )}
        </section>
      </aside>
    </div>
  );
}

/** The headline (it does not jump, see pickHeadline) and whether the full red list is open. */
function useRedLines(lines: PanelLine[]) {
  const [open, setOpen] = useState(false);
  const previous = useRef<string | null>(null);
  const headline = pickHeadline(previous.current, lines);
  previous.current = headline?.control ?? null;
  useEffect(() => {
    if (lines.length <= 1) setOpen(false);
  }, [lines.length]);
  const others = headline ? lines.filter((l) => l.control !== headline.control) : [];
  return { headline, others, open, setOpen };
}

/**
 * The headline line: the most important red line with its action and "+N more". When nothing is
 * red: one calm line. A status line, not a page title.
 */
function Headline({ red, peerWord, actions, busy, compared }: {
  red: ReturnType<typeof useRedLines>; peerWord: string; actions: Actions; busy: boolean; compared: string;
}) {
  const { headline, others, open, setOpen } = red;
  if (!headline) {
    return (
      <div className="pane-head headline">
        <div className="headline-title">
          <Mark />
          <h1>All controls match</h1>
          <span className="muted compared">{compared}</span>
        </div>
      </div>
    );
  }

  const text = lineText(headline, peerWord);
  return (
    <section className="pane-head headline" aria-labelledby="headline-title">
      <div className="headline-title">
        <AlertIcon />
        <h1 id="headline-title">{headline.label}</h1>
        <span className="headline-short">{text.short}</span>
        <span className="muted compared">{compared}</span>
      </div>
      {open && <LineDetails line={headline} peerWord={peerWord} />}
      {(headline.action || others.length > 0) && (
        <div className="row">
          <LineActions line={headline} actions={actions} busy={busy} />
          {others.length > 0 && (
            <>
              <span className="strong">{moreText(others.length)}</span>
              <Button small variant="secondary" onClick={() => setOpen(!open)} aria-expanded={open} aria-controls="red-lines">
                {open ? 'Hide' : 'Show'}
              </Button>
            </>
          )}
        </div>
      )}
    </section>
  );
}

/** Both values (for switches and displays) and the full sentence. */
function LineDetails({ line, peerWord }: { line: PanelLine; peerWord: string }) {
  const text = lineText(line, peerWord);
  return (
    <div className="row-wide">
      {line.you && line.other && (
        <dl className="values">
          <dt>You</dt>
          <dd>{line.you.text}</dd>
          <dt>{peerWord.charAt(0).toUpperCase() + peerWord.slice(1)}</dt>
          <dd>{line.other.text}</dd>
        </dl>
      )}
      <p className="muted">{line.message ?? text.long}</p>
    </div>
  );
}

/** One red line in the open list: label, what differs, where and since when, its action. */
function ControlLine({ line, peerWord, actions, busy }: { line: PanelLine; peerWord: string; actions: Actions; busy: boolean }) {
  const id = `line-${line.control}`;
  const where = [line.group_label, `since ${timeOfDay(line.since)}`].filter(Boolean).join(', ');
  const text = lineText(line, peerWord);
  return (
    <section className="item" aria-labelledby={id}>
      <div className="item-title">
        <h2 id={id}>{line.label}</h2>
        {/* A switch shows both values below; the sentence would repeat them. */}
        {!(line.kind === 'set_both' && line.you && line.other) && <span>{text.short}</span>}
        <span className="muted small">{where.charAt(0).toUpperCase() + where.slice(1)}</span>
      </div>
      <LineDetails line={line} peerWord={peerWord} />
      {line.action && <div className="actions"><LineActions line={line} actions={actions} busy={busy} /></div>}
    </section>
  );
}

/** One action per line: Set both (one button per value), Press X here only, or Release it. */
function LineActions({ line, actions, busy }: { line: PanelLine; actions: Actions; busy: boolean }) {
  const a = line.action;
  if (!a) return null;
  switch (a.type) {
    case 'set_both':
      return (
        <span className="row">
          {a.options.map((o, i) => (
            <Button key={o.value} variant={i === 0 ? 'primary' : 'secondary'} onClick={() => void actions.setBoth(a.var, o.value)} disabled={busy}>
              {actionLabel(a, o.text)}
            </Button>
          ))}
        </span>
      );
    case 'press_here':
      return <Button onClick={() => void actions.pressHere(a.var)} disabled={busy}>{actionLabel(a)}</Button>;
    case 'release':
      return <Button onClick={() => void actions.releaseIt(a.var)} disabled={busy}>{actionLabel(a)}</Button>;
  }
}

/** "You have control" after a control change, with the open differences as plain information. */
function useNotice(data: ControlNotice | null) {
  const [dismissedAt, setDismissedAt] = useState<string | null>(null);
  useEffect(() => {
    if (!data) setDismissedAt(null);
  }, [data]);
  return {
    data,
    visible: !!data && data.at !== dismissedAt,
    dismiss: () => setDismissedAt(data?.at ?? null),
  };
}

/** One line: "You have control · 3 differences at handover · Show", and OK to dismiss it. */
function ControlChange({ notice, peerWord, onDismiss }: { notice: ControlNotice; peerWord: string; onDismiss: () => void }) {
  const [open, setOpen] = useState(false);
  const count = notice.groups.length + notice.vars.length;
  return (
    <section className="note" aria-labelledby="control-change">
      <div className="note-stack">
        <div className="note-row">
          <span id="control-change" className="strong">You have control</span>
          <span className="sep" aria-hidden="true">·</span>
          <span>{handoverLine(count)}</span>
          {count > 0 && (
            <Button small variant="secondary" onClick={() => setOpen(!open)} aria-expanded={open}>{open ? 'Hide' : 'Show'}</Button>
          )}
          <Button small variant="secondary" onClick={onDismiss}>OK</Button>
        </div>
        {open && <p className="muted small">{handoverDetail(notice, peerWord)}</p>}
      </div>
    </section>
  );
}

/**
 * Everything that is not red, collapsed under the red lines: connection, amber lines (one per hardware
 * fight, fuel drift, not synced yet, outside the aircraft) and one grey line for the indicators.
 */
function CollapsedLines({ status, panel, peerWord, isPf, confirmPeerLost, actions, busy, now }: {
  status: Status; panel: Panel; peerWord: string; isPf: boolean; confirmPeerLost: boolean; actions: Actions; busy: boolean; now: number;
}) {
  const peer = (status.crew ?? []).find((c) => !c.you);
  const fuel = driftWarningText(status.owned ?? []);
  const lines: ReactNode[] = [];
  if (status.server === 'reconnecting') {
    lines.push(<span key="server" className="warning">Connection to the server lost. Reconnecting. Both sims keep flying, just not synced.</span>);
  }
  if (peer && peer.state === 'lost' && status.peer_lost_at) {
    lines.push(<span key="peer" className="warning">{peerLostText(peer, status.peer_lost_at, status.session_ttl_ms ?? 300000, now)}</span>);
  }
  const physics = status.physics;
  if (physics?.mode === 'released') {
    lines.push(<span key="released" className="differs">You took the aircraft while the {peerWord} was lost: you are flying unsynced. Crosscheck asks the server for control as soon as it is back.</span>);
  }
  if (physics?.no_stream) {
    lines.push(<span key="no-stream" className="warning">No aircraft data from the {peerWord} for 2 seconds. Your aircraft holds where it is.</span>);
  }
  if (physics && physics.refused.length > 0) {
    lines.push(<span key="refused" className="warning">The sim refused part of the aircraft data ({physics.refused.map((v) => v.toLowerCase()).join(', ')}). Position still follows.</span>);
  }
  if (physics?.levers_moved && !isPf) {
    lines.push(<span key="levers-moved" className="warning">Your thrust levers moved. The pilot flying's position is used.</span>);
  }
  if (levers(physics) && !isPf) {
    lines.push(<span key="levers" className="warning">Your thrust levers differ from the {peerWord}'s. Match them before you take control.</span>);
  }
  if (confirmPeerLost && !isPf && physics?.mode !== 'pf_lost') {
    lines.push(
      <span key="confirm" className="note-row">
        <span className="warning">The {peerWord} is not connected. Take control anyway?</span>
        <Button small onClick={() => void actions.takeControl(true)} disabled={busy}>Take control anyway</Button>
      </span>,
    );
  }
  for (const a of panel.amber) lines.push(<span key={a.key} className="warning">{amberText(a)}</span>);
  if (fuel) lines.push(<span key="fuel" className="warning">{fuel}</span>);
  if (panel.not_synced.count > 0) {
    lines.push(
      <Expandable key="not-synced" tone="warning" text={notSyncedLine(panel.not_synced.count)} listName="Not synced yet"
        items={panel.not_synced.items} detail={notSyncedDetail} />,
    );
  }
  if (panel.outside.count > 0) {
    lines.push(
      <Expandable key="outside" tone="warning" text={outsideLine(panel.outside.count)} listName="Outside the aircraft"
        items={panel.outside.items} detail={(i) => `${itemValues(i, peerWord)}. Set it by hand in both cockpits if it matters.`} />,
    );
  }
  if (panel.indicators.count > 0) {
    lines.push(
      <Expandable key="indicators" tone="muted" text={indicatorsLine(panel.indicators.count)} listName="Indicators that differ"
        items={panel.indicators.items} detail={(i) => itemValues(i, peerWord)} />,
    );
  }
  if (lines.length === 0) return null;
  return (
    <div className="notes" role="status">
      {lines.map((l, i) => <div key={i} className="note">{l}</div>)}
    </div>
  );
}

/** Levers whose hardware position is known and differs (never-moved hardware is only asked for at "I have control"). */
const levers = (p: Status['physics']) => !!p && p.levers.some((l) => l.mine !== null);

/** How long a click on "I have control" waits for the thrust levers to match (owner decision 2026-11-05). */
const LEVER_ASK_MS = 30_000;

/** One collapsed line with a count; the controls behind it on click. */
function Expandable({ tone, text, listName, items, detail }: {
  tone: 'warning' | 'muted'; text: string; listName: string; items: PanelItem[]; detail: (i: PanelItem) => string;
}) {
  const [open, setOpen] = useState(false);
  return (
    <span className="note-stack">
      <span className="note-row">
        <span className={tone}>{text}</span>
        <Button small variant="secondary" onClick={() => setOpen(!open)} aria-expanded={open}>{open ? 'Hide' : 'Show'}</Button>
      </span>
      {open && (
        <ul className="details" aria-label={listName}>
          {items.map((i) => (
            <li key={i.control}>
              <span>{i.label}</span>
              <span className="muted">{detail(i)}</span>
            </li>
          ))}
        </ul>
      )}
    </span>
  );
}
