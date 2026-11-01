import { Button, Header } from '../components/parts';
import type { Actions, Preflight as PreflightData, Status } from '../types';
import { preflightExplanation, preflightResult } from '../words';

const NUMBERS = ['No', 'One', 'Two', 'Three', 'Four', 'Five'];

export function Preflight({ status, actions, busy, error }: { status: Status; actions: Actions; busy: boolean; error: string | null }) {
  const p = status.preflight as PreflightData;
  const other = 'captain';
  const open = p.rows.filter((r) => r.result !== 'same');
  const blocking = p.rows.filter((r) => r.result === 'reject');
  const count = (n: number) => NUMBERS[n] ?? String(n);

  const lead = !p.joinable
    ? `Both cockpits must run the same versions before anything is synced. ${count(blocking.length)} ${blocking.length === 1 ? 'difference has' : 'differences have'} to be fixed before you can join.`
    : open.length === 0
      ? 'Both cockpits must run the same versions before anything is synced. Everything matches.'
      : `Both cockpits must run the same versions before anything is synced. ${count(open.length)} ${open.length === 1 ? 'difference needs' : 'differences need'} your attention.`;

  return (
    <>
      <Header>
        <span className="header-note">Joining <span className="mono ink">{p.code}</span></span>
      </Header>
      <main className="main main-preflight">
        <div className="intro">
          <h1>{p.joinable ? "Your setup matches the captain's" : "Your setup does not match the captain's"}</h1>
          <p className="lead" style={{ maxWidth: 600 }}>{lead}</p>
        </div>
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Item</th>
                <th>You</th>
                <th>Captain</th>
                <th>Result</th>
              </tr>
            </thead>
            <tbody>
              {p.rows.map((r) => (
                <tr key={r.field}>
                  <td>{r.item}</td>
                  <td className="mono">{r.you}</td>
                  <td className="mono">{r.other}</td>
                  <td className={r.result === 'reject' ? 'differs' : r.result === 'warn' ? 'warning' : undefined}>{preflightResult(r)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {open.map((r) => (
          <p key={r.field} className="prose">{preflightExplanation(r, other)}</p>
        ))}
        {error && <p className="warning">{error}</p>}
        <div className="row">
          {p.joinable ? (
            <Button onClick={() => void actions.join(p.code)} disabled={busy}>Join session</Button>
          ) : (
            <Button onClick={() => void actions.check(p.code)} disabled={busy}>Check again</Button>
          )}
          <Button variant="secondary" onClick={() => void actions.cancel()} disabled={busy}>Cancel</Button>
        </div>
      </main>
    </>
  );
}
