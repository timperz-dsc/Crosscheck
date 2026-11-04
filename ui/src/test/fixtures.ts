import { vi } from 'vitest';
import type { BridgeState } from '../api';
import type { Actions, Panel, PanelLine, Status } from '../types';

export const NOW = new Date('2026-11-01T12:43:30').getTime();
export const at = (secondsAgo: number) => new Date(NOW - secondsAgo * 1000).toISOString();

export function idle(over: Partial<Status> = {}): Status {
  return {
    phase: 'idle',
    bridge_version: '0.3.0',
    last_error: null,
    log: { folder: 'C:\Users\pilot\AppData\Local\Crosscheck\logs', file: 'C:\Users\pilot\AppData\Local\Crosscheck\logs\bridge-1.jsonl' },
    this_computer: {
      sim: { connected: true, name: 'MSFS 2024' },
      aircraft: { name: 'Fenix A320', app_version: '3.0.0.7613', package_version: '3.0.0.7613', reading: false },
      profile: { id: 'fenix-a320', builds: ['3.0.0.7613'], hash: 'sha256:7f3a9c' },
      server: 'ws://127.0.0.1:8787/ws',
    },
    start_blocked: null,
    preflight: null,
    join_code: null,
    seat: null,
    seat_side: null,
    role: null,
    server: 'disconnected',
    rtt_ms: null,
    peer: null,
    ...over,
  };
}

export function inSession(over: Partial<Status> = {}): Status {
  return idle({
    phase: 'session',
    join_code: 'K7RX2M',
    seat: 'guest',
    seat_side: 'fo',
    role: 'pm',
    is_pf: false,
    server: 'connected',
    rtt_ms: 38,
    peer: 'joined',
    crew: [
      { side: 'fo', you: true, role: 'pm', state: 'connected' },
      { side: 'captain', you: false, role: 'pf', state: 'connected' },
    ],
    session_ttl_ms: 300000,
    last_compared_at: at(2),
    groups: [
      { id: 'overhead', label: 'Overhead', differs: 0 },
      { id: 'fcu', label: 'FCU', differs: 0 },
      { id: 'mip', label: 'Main panel', differs: 0 },
    ],
    owned: [{ var: 'aircraft.fuel.left.amount.kg', label: 'Fuel', unit: 'kg', delta: -12 }],
    panel: panel(),
    control_notice: null,
    ...over,
  });
}

/** An empty panel, with the parts a test sets. */
export function panel(over: Partial<Panel> = {}): Panel {
  return {
    red: [], not_synced: { count: 0, items: [] }, outside: { count: 0, items: [] }, amber: [], indicators: { count: 0, items: [] }, log: [],
    ...over,
  };
}

/** Engine 1 bleed: on for you, off for the captain, Set both. */
export const BLEED: PanelLine = {
  control: 'S_OH_PNEUMATIC_ENG1_BLEED',
  label: 'Engine 1 bleed',
  group: 'overhead',
  group_label: 'Overhead',
  since: at(20),
  priority: 3,
  kind: 'set_both',
  where: null,
  you: { value: 1, text: 'ON' },
  other: { value: 0, text: 'OFF' },
  action: { type: 'set_both', var: 'S_OH_PNEUMATIC_ENG1_BLEED', options: [{ value: 1, text: 'ON' }, { value: 0, text: 'OFF' }] },
  message: null,
  reason: 'refused',
};

/** A red line of another kind. */
export const line = (over: Partial<PanelLine>): PanelLine => ({ ...BLEED, action: null, you: null, other: null, ...over });

type Mocked = { [K in keyof Actions]: ReturnType<typeof vi.fn> & Actions[K] };

export function actions(): Mocked {
  const ok = () => vi.fn(async () => {});
  return {
    create: ok(), check: ok(), join: ok(), cancel: ok(), leave: ok(), takeControl: ok(), giveControl: ok(), setBoth: ok(), pressHere: ok(), releaseIt: ok(), setServer: ok(), applyUpdate: ok(),
  } as unknown as Mocked;
}

export function bridge(status: Status | null, over: Partial<BridgeState> = {}): BridgeState & { actions: Mocked } {
  return { status, bridgeDown: false, error: null, confirmPeerLost: false, busy: false, actions: actions(), ...over } as BridgeState & { actions: Mocked };
}
