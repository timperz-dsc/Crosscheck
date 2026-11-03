import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { idle, inSession, panel } from './test/fixtures';
import type { Status } from './types';

// The real data hook against a mocked bridge: status polling, POSTs and the events socket.

class FakeSocket {
  static last: FakeSocket | null = null;
  onmessage: ((m: { data: string }) => void) | null = null;
  onclose: (() => void) | null = null;
  constructor(public url: string) {
    FakeSocket.last = this;
  }
  close() {}
  emit(e: object) {
    this.onmessage?.({ data: JSON.stringify(e) });
  }
}

let status: Status;
const posts: { path: string; body: unknown }[] = [];

beforeEach(() => {
  posts.length = 0;
  status = idle();
  vi.stubGlobal('WebSocket', FakeSocket);
  vi.stubGlobal('fetch', vi.fn(async (path: string, init?: RequestInit) => {
    if (init?.method === 'POST') {
      posts.push({ path, body: JSON.parse(String(init.body)) });
      if (path === '/api/session/create') status = { ...idle(), phase: 'waiting', join_code: 'K7RX2M', server: 'connected' };
      if (path === '/api/control/take') return new Response(JSON.stringify({ ok: false, error: 'Not in a session.' }), { status: 409 });
      return new Response(JSON.stringify({ ok: true }));
    }
    return new Response(JSON.stringify(status));
  }));
});

afterEach(() => vi.unstubAllGlobals());

describe('app with the bridge API', () => {
  it('loads the status, creates a session and shows the code', async () => {
    render(<App />);
    expect(await screen.findByText('Start a shared cockpit')).toBeInTheDocument();
    expect(FakeSocket.last?.url).toMatch(/\/api\/events$/);
    await userEvent.click(screen.getByRole('button', { name: 'Create session' }));
    expect(posts[0]).toEqual({ path: '/api/session/create', body: {} });
    expect(await screen.findByText('Waiting for the first officer')).toBeInTheDocument();
  });

  it('an event refreshes the status (the log comes with it) and asks for the confirm when the PF is lost', async () => {
    status = inSession();
    render(<App />);
    expect(await screen.findByText('All controls match')).toBeInTheDocument();
    status = inSession({ panel: panel({ log: [{ type: 'action_confirmed', text: 'APU MASTER on by you. Confirmed.', at: new Date().toISOString() }] }) });
    act(() => FakeSocket.last!.emit({ type: 'action_confirmed', message: 'APU MASTER on by you. Confirmed.', at: new Date().toISOString(), data: null }));
    expect(await screen.findByText('APU MASTER on by you. Confirmed.')).toBeInTheDocument();
    act(() => FakeSocket.last!.emit({ type: 'control_denied', message: 'x', at: new Date().toISOString(), data: { reason: 'peer_lost' } }));
    expect(await screen.findByText('The captain is not connected. Take control anyway?')).toBeInTheDocument();
  });

  it('shows the error sentence when a request is refused', async () => {
    status = inSession();
    render(<App />);
    await userEvent.click(await screen.findByRole('button', { name: 'I have control' }));
    expect(await screen.findByText('Not in a session.')).toHaveClass('warning');
    expect(posts.at(-1)).toEqual({ path: '/api/control/take', body: { confirm_peer_lost: false } });
  });
});
