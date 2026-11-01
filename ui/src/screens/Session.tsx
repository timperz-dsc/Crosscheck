import { useEffect, useState } from 'react';
import { AlertIcon, Button, Line, Mark } from '../components/parts';
import type { Actions, ControlNotice, Desync, RecentItem, Status } from '../types';
import {
  SideName, desyncExplanation, differsHeading, driftText, lastCompared, otherSide, peerLostText, roleName, sideName, timeOfDay,
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

export function Session({ status, recent, actions, busy, error, confirmPeerLost, now }: Props) {
  const desyncs = status.desyncs ?? [];
  const crew = status.crew ?? [];
  const me = crew.find((c) => c.you);
  const peer = crew.find((c) => !c.you);
  const peerWord = sideName(peer?.side ?? otherSide(status));
  const isPf = status.is_pf ?? status.role === 'pf';
  const notice = useNotice(status.control_notice ?? null);

  return (
    <>
      <SessionHeader status={status} actions={actions} busy={busy} />
      {status.server === 'reconnecting' && (
        <Line tone="warning">Connection to the server lost. Reconnecting. Both sims keep flying, just not synced.</Line>
      )}
      {peer && peer.state === 'lost' && status.peer_lost_at && (
        <Line tone="warning">{peerLostText(peer, status.peer_lost_at, status.session_ttl_ms ?? 300000, now)}</Line>
      )}
      {confirmPeerLost && !isPf && (
        <div className="line" role="status">
          <span className="warning">The {peerWord} is not connected. Take control anyway?</span>
          <Button small onClick={() => void actions.takeControl(true)} disabled={busy}>Take control anyway</Button>
        </div>
      )}
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
