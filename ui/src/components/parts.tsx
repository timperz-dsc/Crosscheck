import type { ButtonHTMLAttributes, ReactNode } from 'react';
import type { Actions, Status } from '../types';
import { serverText, simText } from '../words';

/** The Crosscheck mark, in the theme's ink and green. */
export function Mark({ size = 18 }: { size?: number }) {
  return (
    <svg viewBox="0 0 100 100" width={size} height={size} aria-hidden="true" style={{ flex: 'none' }}>
      <path className="mark-ink" d="M20 50 L36 66" strokeWidth="13" strokeLinecap="round" fill="none" />
      <path className="mark-green" d="M50 65.5 L82 30" strokeWidth="13" strokeLinecap="round" fill="none" />
    </svg>
  );
}

/** The one alert icon (4-desync), in the theme's red. */
export function AlertIcon({ size = 18 }: { size?: number }) {
  return (
    <svg className="alert-icon" viewBox="0 0 24 24" width={size} height={size} aria-hidden="true">
      <circle cx="12" cy="12" r="10" fill="none" stroke="currentColor" strokeWidth="2" />
      <path d="M12 7 L12 13" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" fill="none" />
      <circle cx="12" cy="16.8" r="1.3" fill="currentColor" />
    </svg>
  );
}

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'secondary'; small?: boolean };

export function Button({ variant = 'primary', small = false, className, type = 'button', ...rest }: ButtonProps) {
  return <button type={type} className={['btn', `btn-${variant}`, small ? 'btn-small' : '', className ?? ''].join(' ').trim()} {...rest} />;
}

/** One status line at the top of the content. */
export function Line({ children, tone }: { children: ReactNode; tone?: 'warning' }) {
  return (
    <div className="line" role="status">
      <span className={tone === 'warning' ? 'warning' : undefined}>{children}</span>
    </div>
  );
}

/** The window: the screen above, the status bar below. Nothing outside it scrolls. */
export function Shell({ children, bar }: { children: ReactNode; bar: ReactNode }) {
  return (
    <div className="shell">
      <div className="content">{children}</div>
      {bar}
    </div>
  );
}

/**
 * The status bar: where we are (session code), the server connection, the sim, the version, and Leave
 * while in a session. Text only; a problem is in the warning colour.
 */
export function StatusBar({ status, actions, busy }: { status: Status | null; actions?: Actions; busy?: boolean }) {
  if (!status) {
    return (
      <footer className="statusbar">
        <span>Crosscheck</span>
      </footer>
    );
  }
  const inSession = status.phase === 'waiting' || status.phase === 'session';
  const sim = status.this_computer.sim;
  return (
    <footer className="statusbar">
      {status.phase === 'preflight' && status.preflight ? (
        <span>Joining <span className="mono">{status.preflight.code}</span></span>
      ) : inSession || status.phase === 'ended' ? (
        <span>Session <span className="mono">{status.join_code}</span>{status.phase === 'ended' ? ', ended' : ''}</span>
      ) : (
        <span>Not in a session</span>
      )}
      {inSession && <span className={status.server === 'connected' ? undefined : 'warning'}>{serverText(status)}</span>}
      <span className={sim.connected ? undefined : 'warning'}>{simText(status)}</span>
      <span className="statusbar-end">
        <span>Crosscheck {status.bridge_version}</span>
        {inSession && actions && (
          <Button variant="secondary" small onClick={() => void actions.leave()} disabled={busy}>Leave</Button>
        )}
      </span>
    </footer>
  );
}
