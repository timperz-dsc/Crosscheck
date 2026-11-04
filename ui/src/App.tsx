import { useEffect, useState } from 'react';
import { useBridge, type BridgeState } from './api';
import { Button, Line, Shell, StatusBar } from './components/parts';
import { Preflight } from './screens/Preflight';
import { Session } from './screens/Session';
import { Start } from './screens/Start';
import { Waiting } from './screens/Waiting';

/** Picks the screen from the bridge's phase; every screen sits in the same window with the status bar. */
export function Screens({ bridge, now }: { bridge: BridgeState; now: number }) {
  const { status, actions, busy, error, confirmPeerLost, bridgeDown } = bridge;

  if (!status) {
    return (
      <Shell bar={<StatusBar status={null} />}>
        {bridgeDown && <Line tone="warning">The Crosscheck bridge is not running. Start crosscheck.exe, then reload this page.</Line>}
      </Shell>
    );
  }

  const down = bridgeDown && <Line tone="warning">The Crosscheck bridge stopped answering. Both sims keep flying, just not synced.</Line>;
  const bar = <StatusBar status={status} actions={actions} busy={busy} />;
  switch (status.phase) {
    case 'preflight':
      return <Shell bar={bar}>{down}<Preflight status={status} actions={actions} busy={busy} error={error} /></Shell>;
    case 'waiting':
      return <Shell bar={bar}>{down}<Waiting status={status} /></Shell>;
    case 'session':
      return (
        <Shell bar={bar}>
          {down}
          <Session status={status} actions={actions} busy={busy} error={error} confirmPeerLost={confirmPeerLost} now={now} />
        </Shell>
      );
    case 'ended':
      return (
        <Shell bar={bar}>
          <main className="page">
            <div className="page-inner">
              <div className="intro">
                <h1>The session has ended</h1>
                <p className="lead">The other pilot left or the server closed the session. Your sim keeps flying as it is.</p>
              </div>
              {status.log && (
                <p className="muted">
                  Your log of this session is in <span className="mono">{status.log.folder}</span>. Send the newest file there if something went wrong.
                </p>
              )}
              <div className="actions">
                <Button onClick={() => void actions.leave()} disabled={busy}>Back to start</Button>
              </div>
            </div>
          </main>
        </Shell>
      );
    default:
      return <Shell bar={bar}>{down}<Start status={status} actions={actions} busy={busy} error={error} /></Shell>;
  }
}

export function App() {
  const bridge = useBridge();
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);
  return <Screens bridge={bridge} now={now} />;
}
