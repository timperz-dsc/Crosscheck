import { useCallback, useEffect, useRef, useState } from 'react';
import type { Actions, BridgeEvent, RecentItem, Status } from './types';

/** Events that belong in the Recent list (same list as the bridge's /api/recent). */
export const RECENT_TYPES = new Set([
  'action_confirmed', 'action_failed', 'action_applied', 'corrected', 'overridden', 'desync', 'desync_cleared',
  'control_changed', 'takeover_pressed', 'peer_joined', 'peer_lost', 'peer_resumed', 'peer_left', 'peer_expired',
]);

async function post(path: string, body: unknown = {}): Promise<void> {
  const r = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!r.ok) {
    const answer = (await r.json().catch(() => ({}))) as { error?: string };
    throw new Error(answer.error ?? `The bridge answered ${r.status}.`);
  }
}

export interface BridgeState {
  status: Status | null;
  recent: RecentItem[];
  /** The bridge itself does not answer (not running). */
  bridgeDown: boolean;
  /** The last request failed; the sentence from the bridge. */
  error: string | null;
  /** A take-control request was refused because the PF is lost: ask for the explicit confirm. */
  confirmPeerLost: boolean;
  busy: boolean;
  actions: Actions;
}

/** Polls /api/status every second and listens to /api/events. */
export function useBridge(pollMs = 1000): BridgeState {
  const [status, setStatus] = useState<Status | null>(null);
  const [recent, setRecent] = useState<RecentItem[]>([]);
  const [bridgeDown, setBridgeDown] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmPeerLost, setConfirmPeerLost] = useState(false);
  const [busy, setBusy] = useState(false);
  const alive = useRef(true);

  const refresh = useCallback(async () => {
    try {
      const r = await fetch('/api/status');
      if (!r.ok) throw new Error(String(r.status));
      const s = (await r.json()) as Status;
      if (!alive.current) return;
      setStatus(s);
      setBridgeDown(false);
    } catch {
      if (alive.current) setBridgeDown(true);
    }
  }, []);

  useEffect(() => {
    alive.current = true;
    void refresh();
    fetch('/api/recent').then((r) => r.json()).then((items: RecentItem[]) => alive.current && setRecent(items)).catch(() => {});
    const timer = setInterval(() => void refresh(), pollMs);

    let ws: WebSocket | null = null;
    let retry: ReturnType<typeof setTimeout> | undefined;
    const connect = () => {
      const proto = location.protocol === 'https:' ? 'wss' : 'ws';
      ws = new WebSocket(`${proto}://${location.host}/api/events`);
      ws.onmessage = (m) => {
        const e = JSON.parse(String(m.data)) as BridgeEvent;
        if (RECENT_TYPES.has(e.type)) setRecent((r) => [{ type: e.type, message: e.message, at: e.at }, ...r].slice(0, 50));
        if (e.type === 'control_denied' && e.data?.reason === 'peer_lost') setConfirmPeerLost(true);
        if (e.type === 'control_changed') setConfirmPeerLost(false);
        void refresh();
      };
      ws.onclose = () => {
        if (alive.current) retry = setTimeout(connect, 2000);
      };
    };
    connect();
    return () => {
      alive.current = false;
      clearInterval(timer);
      clearTimeout(retry);
      if (ws) {
        ws.onclose = null;
        ws.close();
      }
    };
  }, [pollMs, refresh]);

  const run = useCallback(
    (fn: () => Promise<void>) => async () => {
      setBusy(true);
      setError(null);
      try {
        await fn();
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
      } finally {
        setBusy(false);
        await refresh();
      }
    },
    [refresh],
  );

  const actions: Actions = {
    create: () => run(() => post('/api/session/create'))(),
    check: (code) => run(() => post('/api/session/check', { code }))(),
    join: (code) => run(() => post('/api/session/join', { code }))(),
    cancel: () => run(() => post('/api/session/cancel'))(),
    leave: () => run(async () => {
      await post('/api/session/leave');
      setRecent([]);
    })(),
    takeControl: (confirm = false) => run(async () => {
      await post('/api/control/take', { confirm_peer_lost: confirm });
      if (confirm) setConfirmPeerLost(false);
    })(),
    giveControl: () => run(() => post('/api/control/give'))(),
    setBoth: (variable, value) => run(() => post('/api/set', { var: variable, value }))(),
  };

  return { status, recent, bridgeDown, error, confirmPeerLost, busy, actions };
}
