import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertIcon, Button, Mark } from '../components/parts';
import { pickHeadline } from '../panel';
import type { Actions, ControlNotice, Panel, PanelItem, PanelLine, Status } from '../types';
import {
  SideName, actionLabel, amberText, driftText, driftWarningText, indicatorsLine, itemValues, lastCompared, lineText, moreText,
  notSyncedDetail, notSyncedLine, otherSide, outsideLine, peerLostText, roleName, sideName, timeOfDay,
} from '../words';
import { SessionHeader } from './Waiting';

interface Props {
  status: Status;
  actions: Actions;
  busy: boolean;
  error: string | null;
  confirmPeerLost: boolean;
  /** Current time in ms (ticks every second). */
  now: number;
}

const EMPTY: Panel = {
  red: [], not_synced: { count: 0, items: [] }, outside: { count: 0, items: [] }, amber: [], indicators: { count: 0, items: [] }, log: [],
};

/**
 * The session panel (CRO-34), for a quick glance in flight: one headline line with the most important
 * red line and its action, the full red list on demand, everything else collapsed below, and the
 * Panels summary and the log on the right.
 */
export function Session({ status, actions, busy, error, confirmPeerLost, now }: Props) {
  const panel = status.panel ?? EMPTY;
  const crew = status.crew ?? [];
  const me = crew.find((c) => c.you);
  const peer = crew.find((c) => !c.you);
  const peerWord = sideName(peer?.side ?? otherSide(status));
  const isPf = status.is_pf ?? status.role === 'pf';
  const notice = useNotice(status.control_notice ?? null);

  return (
    <>
      <SessionHeader status={status} actions={actions} busy={busy} />
      <main className="main main-session">
        <div className="session-col">
          <RedLines lines={panel.red} peerWord={peerWord} actions={actions} busy={busy} compared={lastCompared(status.last_compared_at, now)} />

          {notice.visible && notice.data && <ControlChange notice={notice.data} peerWord={peerWord} onDismiss={notice.dismiss} />}

          <CollapsedLines status={status} panel={panel} peerWord={peerWord} isPf={isPf} confirmPeerLost={confirmPeerLost} actions={actions} busy={busy} now={now} />

          <section className="stack-wide">
            <dl className="list list-crew">
              {[...crew].sort((a, b) => (a.side === 'captain' ? 0 : 1) - (b.side === 'captain' ? 0 : 1)).map((c) => (
                <div key={c.side} style={{ display: 'contents' }}>
                  <dt>{SideName(c.side)}{c.you ? ' (you)' : ''}</dt>
                  <dd className="muted">{roleName(c.role)}{c.state === 'lost' ? ', lost connection' : ''}</dd>
                </div>
              ))}
            </dl>
            {me && peer && (
              <div className="row-wide">
                {isPf ? (
                  <>
                    <Button onClick={() => void actions.giveControl()} disabled={busy || peer.state !== 'connected'}>You have control</Button>
                    <span className="hint">Gives control to the {peerWord} at once.</span>
                  </>
                ) : (
                  <>
                    <Button onClick={() => void actions.takeControl(false)} disabled={busy}>I have control</Button>
                    <span className="hint">Takes over at once. The takeover button on your sidestick does the same.</span>
                  </>
                )}
              </div>
            )}
            {error && <p className="warning">{error}</p>}
          </section>
        </div>

        <aside className="session-aside">
          <section className="stack" style={{ gap: 10 }} aria-labelledby="panels-title">
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
          <section className="log-section" aria-labelledby="log-title">
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
      </main>
    </>
  );
}

/**
 * The headline (the most important red line, with its action and "+N more") and, when opened, every
 * other red line. When nothing is red: one calm line.
 */
function RedLines({ lines, peerWord, actions, busy, compared }: {
  lines: PanelLine[]; peerWord: string; actions: Actions; busy: boolean; compared: string;
}) {
  const [open, setOpen] = useState(false);
  const previous = useRef<string | null>(null);
  const headline = pickHeadline(previous.current, lines);
  previous.current = headline?.control ?? null;
  useEffect(() => {
    if (lines.length <= 1) setOpen(false);
  }, [lines.length]);

  if (!headline) {
    return (
      <div className="headline">
        <div className="headline-title">
          <Mark size={30} />
          <h1>All controls match</h1>
        </div>
        <p className="muted">{compared}</p>
      </div>
    );
  }

  const others = lines.filter((l) => l.control !== headline.control);
  const text = lineText(headline, peerWord);
  return (
    <>
      <section className="headline" aria-labelledby="headline-title">
        <div className="headline-title">
          <AlertIcon />
          <h1 id="headline-title">{headline.label}</h1>
          <span className="headline-short">{text.short}</span>
        </div>
        {open && <LineDetails line={headline} peerWord={peerWord} />}
        <div className="row">
          <LineActions line={headline} actions={actions} busy={busy} />
          {others.length > 0 && (
            <span className="note-row">
              <span className="strong">{moreText(others.length)}</span>
              <Button small variant="secondary" onClick={() => setOpen(!open)} aria-expanded={open} aria-controls="red-lines">
                {open ? 'Hide' : 'Show'}
              </Button>
            </span>
          )}
        </div>
        <p className="muted">{compared}</p>
      </section>
      {open && others.length > 0 && (
        <div className="blocks" id="red-lines">
          {others.map((l) => <ControlLine key={l.control} line={l} peerWord={peerWord} actions={actions} busy={busy} />)}
        </div>
      )}
    </>
  );
}

/** Both values (for switches and displays) and the full sentence. */
function LineDetails({ line, peerWord }: { line: PanelLine; peerWord: string }) {
  const text = lineText(line, peerWord);
  return (
    <>
      {line.you && line.other && (
        <dl className="values">
          <dt>You</dt>
          <dt>{peerWord.charAt(0).toUpperCase() + peerWord.slice(1)}</dt>
          <dd>{line.you.text}</dd>
          <dd>{line.other.text}</dd>
        </dl>
      )}
      <p className="prose">{line.message ?? text.long}</p>
    </>
  );
}

function ControlLine({ line, peerWord, actions, busy }: { line: PanelLine; peerWord: string; actions: Actions; busy: boolean }) {
  const id = `line-${line.control}`;
  const where = [line.group_label, `since ${timeOfDay(line.since)}`].filter(Boolean).join(', ');
  const text = lineText(line, peerWord);
  return (
    <section className="block" aria-labelledby={id}>
      <div className="block-title">
        <h2 id={id}>{line.label}</h2>
        <span className="muted">{where.charAt(0).toUpperCase() + where.slice(1)}</span>
      </div>
      {/* A switch shows both values below; the sentence would repeat them. */}
      {!(line.kind === 'set_both' && line.you && line.other) && <p className="prose">{text.short}</p>}
      <LineDetails line={line} peerWord={peerWord} />
      {line.action && <div className="row"><LineActions line={line} actions={actions} busy={busy} /></div>}
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

function ControlChange({ notice, peerWord, onDismiss }: { notice: ControlNotice; peerWord: string; onDismiss: () => void }) {
  const how = notice.kind === 'give' ? `The ${peerWord} gave you control.` : 'You took control.';
  const open = [...notice.groups, ...notice.vars];
  const differences =
    open.length === 0
      ? 'Both cockpits matched at the handover.'
      : `Open differences at the handover: ${open.join(', ')}. They are listed for your information; nothing was changed.`;
  return (
    <section className="block" aria-labelledby="control-change">
      <div className="block-title">
        <h2 id="control-change">You have control</h2>
        {notice.at && <span className="muted">since {timeOfDay(notice.at)}</span>}
      </div>
      <p className="prose">{how} {differences}</p>
      <div className="row">
        <Button variant="secondary" onClick={onDismiss}>OK</Button>
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
  if (confirmPeerLost && !isPf) {
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
