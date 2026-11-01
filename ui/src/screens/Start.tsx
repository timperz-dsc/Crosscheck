import { useState, type FormEvent } from 'react';
import { Button, Header } from '../components/parts';
import type { Actions, Status } from '../types';
import { aircraftText, profileText } from '../words';

export function ThisComputer({ status }: { status: Status }) {
  const pc = status.this_computer;
  return (
    <section className="stack">
      <h2>This computer</h2>
      <dl className="list">
        <dt className="muted">Simulator</dt>
        <dd className={pc.sim.connected ? undefined : 'warning'}>
          {pc.sim.connected ? `Connected to ${pc.sim.name}` : `Not connected. Start ${pc.sim.name} with the ${pc.aircraft.name}.`}
        </dd>
        <dt className="muted">Aircraft</dt>
        <dd>{aircraftText(status)}</dd>
        <dt className="muted">Profile</dt>
        <dd>{profileText(status)}</dd>
        <dt className="muted">Server</dt>
        <dd className="mono">{pc.server}</dd>
        {status.log && (
          <>
            <dt className="muted">Log folder</dt>
            <dd className="mono">{status.log.folder}</dd>
          </>
        )}
      </dl>
    </section>
  );
}

/** The session server address; saved on this computer. Only changeable between sessions (the Start screen). */
export function ServerSetting({ status, actions, busy }: { status: Status; actions: Actions; busy: boolean }) {
  const current = status.this_computer.server;
  const [value, setValue] = useState<string | null>(null);
  const text = value ?? current;
  const changed = text.trim() !== current;

  const save = (e: FormEvent) => {
    e.preventDefault();
    if (!changed) return;
    void actions.setServer(text).then(() => setValue(null));
  };

  return (
    <form className="stack" onSubmit={save}>
      <label htmlFor="server" className="muted">Session server</label>
      <div className="row">
        <input
          id="server"
          className="mono"
          type="text"
          placeholder="wss://your-server.up.railway.app/ws"
          autoComplete="off"
          spellCheck={false}
          value={text}
          onChange={(e) => setValue(e.target.value)}
        />
        <Button type="submit" variant="secondary" disabled={busy || !changed}>Save</Button>
      </div>
      <p className="muted">Both pilots must use the same server. Saved on this computer.</p>
    </form>
  );
}

export function Start({ status, actions, busy, error }: { status: Status; actions: Actions; busy: boolean; error: string | null }) {
  const [code, setCode] = useState('');
  const blocked = status.start_blocked === 'sim_not_connected';
  const problem = error ?? status.last_error;

  const join = (e: FormEvent) => {
    e.preventDefault();
    if (code.length === 6) void actions.check(code);
  };

  return (
    <>
      <Header>
        <span className="header-note">Not in a session</span>
      </Header>
      <main className="main main-start">
        <div className="intro">
          <h1>Start a shared cockpit</h1>
          <p className="lead">Create a session and send the code to your first officer, or join with a code you were given.</p>
        </div>
        <div className="stack-wide">
          <div className="choices">
            <section className="choice">
              <h2>Create</h2>
              <p className="muted">You will be captain and pilot flying. The code appears once the session is open.</p>
              <Button onClick={() => void actions.create()} disabled={blocked || busy}>Create session</Button>
            </section>
            <form className="choice" onSubmit={join}>
              <h2>Join</h2>
              <label htmlFor="code" className="muted">Session code</label>
              <div className="row">
                <input
                  id="code"
                  className="code-input"
                  type="text"
                  placeholder="K7RX2M"
                  maxLength={6}
                  autoComplete="off"
                  spellCheck={false}
                  value={code}
                  onChange={(e) => setCode(e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, ''))}
                />
                <Button type="submit" variant="secondary" disabled={blocked || busy || code.length !== 6}>Join</Button>
              </div>
            </form>
          </div>
          {blocked && <p className="warning">Start the simulator to create or join a session.</p>}
          {!blocked && problem && <p className="warning">{problem}</p>}
        </div>
        <ServerSetting status={status} actions={actions} busy={busy} />
        <ThisComputer status={status} />
      </main>
    </>
  );
}
