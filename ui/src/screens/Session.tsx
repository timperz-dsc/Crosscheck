import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertIcon, Button, Mark } from '../components/parts';
import type { Actions, Advisory, ControlNotice, Desync, RecentItem, Status } from '../types';
import {
  SideName, advisoryText, desyncExplanation, differsHeading, driftText, driftWarningText, effectsText, indicatorsLine, lastCompared,
  notSyncedText, otherSide, peerLostText, roleName, sideName, timeOfDay,
} from '../words';
import { SessionHeader } from './Waiting';

interface Props {
  status: Status;
  recent: RecentItem[];
  actions: Actions;
  busy: boolean;
  error: string | null;
  confirmPeerLost: boolean;
  /** Current time in ms (ticks every second). */
  now: number;
}

/** More "not synced yet" lines than this collapse into one. */
const NOT_SYNCED_MAX_LINES = 3;

export function Session({ status, recent, actions, busy, error, confirmPeerLost, now }: Props) {
  // Red: a pilot's input did not arrive. Quiet: indicators. (The bridge splits them; quiet ones are never red here.)
  const desyncs = (status.desyncs ?? []).filter((d) => d.tier !== 'quiet');
  const indicators = [...(status.indicators ?? []), ...(status.desyncs ?? []).filter((d) => d.tier === 'quiet')];
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
          <div className="heading">
            <div className="heading-title">
              {desyncs.length === 0 ? <Mark size={30} /> : <AlertIcon />}
              <h1>{differsHeading(desyncs.length)}</h1>
            </div>
            <p className="muted">
              {desyncs.length > 0 ? 'Everything else matches. ' : ''}
              {lastCompared(status.last_compared_at, now)}
            </p>
          </div>

          {notice.visible && notice.data && <ControlChange notice={notice.data} peerWord={peerWord} onDismiss={notice.dismiss} />}

          {desyncs.length > 0 && (
            <div className="blocks">
              {desyncs.map((d) => (
                <DesyncBlock key={d.var} desync={d} peerWord={peerWord} actions={actions} busy={busy} />
              ))}
            </div>
          )}

          <AmberLines status={status} peerWord={peerWord} isPf={isPf} confirmPeerLost={confirmPeerLost} actions={actions} busy={busy} now={now} />

          {indicators.length > 0 && <Indicators indicators={indicators} peerWord={peerWord} />}

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

          <section className="stack" style={{ gap: 10 }}>
            <h2>Recent</h2>
            {recent.length === 0 ? (
              <p className="muted">Nothing yet. Every change either of you makes appears here.</p>
            ) : (
              <ol className="recent">
                {recent.slice(0, 8).map((r, i) => (
                  <li key={`${r.at}-${i}`}>
                    <time dateTime={r.at}>{timeOfDay(r.at)}</time>
                    <span>{r.message}</span>
                  </li>
                ))}
              </ol>
            )}
          </section>
        </div>

        <aside className="session-aside">
          <h2>Panels</h2>
          <dl className="panels">
            {(status.groups ?? []).map((g) => (
              <div key={g.id} style={{ display: 'contents' }}>
                <dt className={g.differs > 0 ? 'strong' : undefined}>{g.label}</dt>
                <dd className={g.differs > 0 ? 'differs' : 'muted'}>{g.differs > 0 ? `${g.differs} differs` : 'Match'}</dd>
              </div>
            ))}
            {(status.owned ?? []).map((o) => (
              <div key={o.var} style={{ display: 'contents' }}>
                <dt>{o.label}</dt>
                <dd className="muted">{driftText(o.delta, o.unit)}</dd>
              </div>
            ))}
          </dl>
        </aside>
      </main>
    </>
  );
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

function DesyncBlock({ desync: d, peerWord, actions, busy }: { desync: Desync; peerWord: string; actions: Actions; busy: boolean }) {
  const id = `desync-${d.var}`;
  const where = [d.group_label, `open since ${timeOfDay(d.since)}`].filter(Boolean).join(', ');
  return (
    <section className="block" aria-labelledby={id}>
      <div className="block-title">
        <h2 id={id} className={d.has_label ? undefined : 'mono'}>{d.label}</h2>
        <span className="muted">{where.charAt(0).toUpperCase() + where.slice(1)}</span>
      </div>
      <dl className="values">
        <dt>You</dt>
        <dt>{peerWord.charAt(0).toUpperCase() + peerWord.slice(1)}</dt>
        <dd>{d.you.text}</dd>
        <dd>{d.other.text}</dd>
      </dl>
      <p className="prose">{desyncExplanation(d, peerWord)}</p>
      {d.effects && d.effects.length > 0 && <p className="muted prose">{effectsText(d.effects)}</p>}
      {d.press_here && (
        <div className="row">
          <Button variant="primary" onClick={() => void actions.pressHere(d.var)} disabled={busy}>
            Press {d.label} here only
          </Button>
        </div>
      )}
      {d.writable && d.options.length > 0 && (
        <div className="row">
          {d.options.map((o, i) => (
            <Button key={o.value} variant={i === 0 ? 'primary' : 'secondary'} onClick={() => void actions.setBoth(d.var, o.value)} disabled={busy}>
              Set both to {o.text}
            </Button>
          ))}
        </div>
      )}
    </section>
  );
}

/**
 * Amber lines, always below the red items: connection, a sim that is not connected, hardware
 * fighting, fuel, and controls that are not synced yet (one line each, more than 3 collapse).
 */
function AmberLines({ status, peerWord, isPf, confirmPeerLost, actions, busy, now }: {
  status: Status; peerWord: string; isPf: boolean; confirmPeerLost: boolean; actions: Actions; busy: boolean; now: number;
}) {
  const [open, setOpen] = useState(false);
  const peer = (status.crew ?? []).find((c) => !c.you);
  const advisories = status.advisories ?? [];
  const notSynced = advisories.filter((a) => a.kind === 'not_synced');
  const others = advisories.filter((a) => a.kind !== 'not_synced');
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
  for (const a of others) lines.push(<span key={a.key} className="warning">{advisoryText(a)}</span>);
  if (fuel) lines.push(<span key="fuel" className="warning">{fuel}</span>);
  if (notSynced.length > 0 && notSynced.length <= NOT_SYNCED_MAX_LINES) {
    for (const a of notSynced) lines.push(<span key={a.key} className="warning">{notSyncedText(a)}</span>);
  } else if (notSynced.length > NOT_SYNCED_MAX_LINES) {
    lines.push(
      <span key="not-synced" className="note-stack">
        <span className="note-row">
          <span className="warning">Not synced yet ({notSynced.length})</span>
          <Button small variant="secondary" onClick={() => setOpen(!open)} aria-expanded={open}>{open ? 'Hide' : 'Show'}</Button>
        </span>
        {open && (
          <ul className="details" aria-label="Not synced yet">
            {notSynced.map((a) => <NotSyncedItem key={a.key} advisory={a} />)}
          </ul>
        )}
      </span>,
    );
  }
  if (lines.length === 0) return null;
  return (
    <div className="notes" role="status">
      {lines.map((l, i) => <div key={i} className="note">{l}</div>)}
    </div>
  );
}

function NotSyncedItem({ advisory: a }: { advisory: Advisory }) {
  return (
    <li>
      <span>{a.label ?? a.var}</span>
      <span className="muted">{a.mine ? 'moved by you' : `moved by the ${a.who}`}, {timeOfDay(a.at)}</span>
    </li>
  );
}

/** Quiet: one grey line with a count; details on click. Never red. */
function Indicators({ indicators, peerWord }: { indicators: Desync[]; peerWord: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="notes">
      <div className="note note-stack">
        <span className="note-row">
          <span className="muted">{indicatorsLine(indicators.length)}</span>
          <Button small variant="secondary" onClick={() => setOpen(!open)} aria-expanded={open}>{open ? 'Hide details' : 'Show details'}</Button>
        </span>
        {open && (
          <ul className="details" aria-label="Indicators that differ">
            {indicators.map((d) => (
              <li key={d.var}>
                <span className={d.has_label ? undefined : 'mono'}>{d.label}</span>
                <span className="muted">
                  you {d.you.text}, {peerWord} {d.other.text}, since {timeOfDay(d.since)}
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
