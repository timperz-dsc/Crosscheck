import { useState } from 'react';
import { Button, Header } from '../components/parts';
import type { Actions, Status } from '../types';
import { serverText } from '../words';
import { ThisComputer } from './Start';

export function SessionHeader({ status, actions, busy }: { status: Status; actions: Actions; busy: boolean }) {
  return (
    <Header>
      <div className="header-meta">
        <span className="muted">Session <span className="mono">{status.join_code}</span></span>
        <span className="muted">{serverText(status)}</span>
        <Button variant="secondary" small onClick={() => void actions.leave()} disabled={busy}>Leave</Button>
      </div>
    </Header>
  );
}

export function Waiting({ status, actions, busy }: { status: Status; actions: Actions; busy: boolean }) {
  const [copied, setCopied] = useState(false);
  const code = status.join_code ?? '';

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };

  return (
    <>
      <SessionHeader status={status} actions={actions} busy={busy} />
      <main className="main main-start">
        <div className="intro">
          <h1>Waiting for the first officer</h1>
          <p className="lead">Send this code to your first officer. They enter it under Join on their computer.</p>
        </div>
        <section className="stack">
          <h2>Session code</h2>
          <div className="row-wide">
            <span className="join-code" aria-label="Session code">{code}</span>
            <Button variant="secondary" onClick={() => void copy()}>{copied ? 'Copied' : 'Copy'}</Button>
          </div>
          <p className="muted">Nothing is synced until the first officer has joined.</p>
        </section>
        <ThisComputer status={status} />
      </main>
    </>
  );
}
