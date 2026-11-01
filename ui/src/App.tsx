import { useEffect, useState } from 'react';
import { useBridge, type BridgeState } from './api';
import { Button, Header, Line } from './components/parts';
import { Preflight } from './screens/Preflight';
import { Session } from './screens/Session';
import { Start } from './screens/Start';
import { SessionHeader, Waiting } from './screens/Waiting';

/** Picks the screen from the bridge's phase. */
export function Screens({ bridge, now }: { bridge: BridgeState; now: number }) {
  const { status, recent, actions, busy, error, confirmPeerLost, bridgeDown } = bridge;

  if (!status) {
    return (
      <>
        <Header />
        {bridgeDown && <Line tone="warning">The Crosscheck bridge is not running. Start crosscheck.exe, then reload this page.</Line>}
      </>
    );
  }

  const down = bridgeDown && <Line tone="warning">The Crosscheck bridge stopped answering. Both sims keep flying, just not synced.</Line>;
  switch (status.phase) {
    case 'preflight':
      return <>{down}<Preflight status={status} actions={actions} busy={busy} error={error} /></>;
    case 'waiting':
      return <>{down}<Waiting status={status} actions={actions} busy={busy} /></>;
    case 'session':
      return (
        <>
          {down}
          <Session status={status} recent={recent} actions={actions} busy={busy} error={error} confirmPeerLost={confirmPeerLost} now={now} />
        </>
      );
    case 'ended':
      return (
        <>
          <SessionHeader status={status} actions={actions} busy={busy} />
          <main className="main main-start">
            <div className="intro">
              <h1>The session has ended</h1>
              <p className="lead">The other pilot left or the server closed the session. Your sim keeps flying as it is.</p>
            </div>
            <div className="row">
              <Button onClick={() => void actions.leave()} disabled={busy}>Back to start</Button>
            </div>
          </main>
        </>
      );
    default:
      return <>{down}<Start status={status} actions={actions} busy={busy} error={error} /></>;
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
