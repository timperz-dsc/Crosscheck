import { useState } from 'react';
import { Button } from '../components/parts';
import type { Status } from '../types';
import { ThisComputer } from './Start';

/** The captain waits for the first officer: the code, large (the one big thing on this screen). Leave is in the status bar. */
export function Waiting({ status }: { status: Status }) {
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
    <main className="page">
      <div className="page-inner">
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
      </div>
    </main>
  );
}
