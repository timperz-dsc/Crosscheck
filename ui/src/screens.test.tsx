import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { Screens } from './App';
import { BLEED, NOW, at, bridge, idle, inSession } from './test/fixtures';
import type { Preflight } from './types';

const show = (b: ReturnType<typeof bridge>) => render(<Screens bridge={b} now={NOW} />);

describe('start', () => {
  it('offers create and join and lists this computer', async () => {
    const b = bridge(idle());
    show(b);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Start a shared cockpit');
    expect(screen.getByText('Connected to MSFS 2024')).toBeInTheDocument();
    expect(screen.getByText('Fenix A320, app 3.0.0.7613')).toBeInTheDocument();
    expect(screen.getByText('fenix-a320, made for 3.0.0.7613')).toBeInTheDocument();
    expect(screen.getByText('ws://127.0.0.1:8787/ws')).toHaveClass('mono');

    await userEvent.click(screen.getByRole('button', { name: 'Create session' }));
    expect(b.actions.create).toHaveBeenCalled();

    const join = screen.getByRole('button', { name: 'Join' });
    expect(join).toBeDisabled();
    await userEvent.type(screen.getByLabelText('Session code'), 'k7rx2m');
    expect(screen.getByLabelText('Session code')).toHaveValue('K7RX2M');
    await userEvent.click(join);
    expect(b.actions.check).toHaveBeenCalledWith('K7RX2M');
  });

  it('disables create and join while the sim is not connected, and says why', () => {
    show(bridge(idle({ start_blocked: 'sim_not_connected', this_computer: { ...idle().this_computer, sim: { connected: false, name: 'MSFS 2024' } } })));
    expect(screen.getByRole('button', { name: 'Create session' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Join' })).toBeDisabled();
    expect(screen.getByText('Not connected. Start MSFS 2024 with the Fenix A320.')).toHaveClass('warning');
    expect(screen.getByText('Start the simulator to create or join a session.')).toBeInTheDocument();
  });

  it('shows why a request failed', () => {
    show(bridge(idle({ last_error: 'No session with the code ZZZZZZ. Check the code with the captain.' })));
    expect(screen.getByText('No session with the code ZZZZZZ. Check the code with the captain.')).toHaveClass('warning');
  });
});

describe('waiting for the first officer', () => {
  it('shows the code large with a copy button', async () => {
    const writeText = vi.fn(async () => {});
    const user = userEvent.setup();
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    show(bridge(idle({ phase: 'waiting', join_code: 'K7RX2M', server: 'connected', rtt_ms: 12 })));
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Waiting for the first officer');
    expect(screen.getByLabelText('Session code')).toHaveTextContent('K7RX2M');
    expect(screen.getByLabelText('Session code')).toHaveClass('join-code');
    await user.click(screen.getByRole('button', { name: 'Copy' }));
    expect(writeText).toHaveBeenCalledWith('K7RX2M');
    expect(await screen.findByRole('button', { name: 'Copied' })).toBeInTheDocument();
  });
});

describe('preflight (joiner)', () => {
  type Row = Preflight['rows'][number];
  const rows = (over: Partial<Record<Row['field'], [string, string, Row['result']]>> = {}): Row[] => {
    const base: [Row['field'], string, string, string][] = [
      ['bridge_version', 'Crosscheck bridge', '0.1.0', '0.1.0'],
      ['profile', 'Profile', 'fenix-a320 · 7f3a9c', 'fenix-a320 · 7f3a9c'],
      ['aircraft.package_version', 'Fenix A320 package', '3.0.0.7613', '3.0.0.7613'],
      ['aircraft.app_version', 'Fenix A320 app', '3.0.0.330', '3.0.0.330'],
      ['airac', 'AIRAC', '2611', '2611'],
    ];
    return base.map(([field, item, you, other]) => {
      const o = over[field];
      return { field, item, you: o?.[0] ?? you, other: o?.[1] ?? other, result: o?.[2] ?? 'same' };
    });
  };
  const pre = (r: Row[], joinable = true) => idle({ phase: 'preflight', preflight: { code: 'K7RX2M', joinable, rows: r } });

  it('everything the same: join allowed', async () => {
    const b = bridge(pre(rows()));
    show(b);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent("Your setup matches the captain's");
    expect(screen.getAllByText('Same')).toHaveLength(5);
    await userEvent.click(screen.getByRole('button', { name: 'Join session' }));
    expect(b.actions.join).toHaveBeenCalledWith('K7RX2M');
  });

  it('a warning (AIRAC) is amber with an explanation; join allowed', () => {
    show(bridge(pre(rows({ airac: ['2611', '2610', 'warn'] }))));
    expect(within(screen.getByRole('row', { name: /AIRAC/ })).getByText('Different')).toHaveClass('warning');
    expect(screen.getByText(/The navigation data differs/)).toBeInTheDocument();
    expect(screen.getByText(/One difference needs your attention/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Join session' })).toBeEnabled();
  });

  it('an unknown aircraft version is a warning, join allowed', () => {
    show(bridge(pre(rows({ 'aircraft.app_version': ['unknown', '3.0.0.330', 'warn'] }))));
    expect(within(screen.getByRole('row', { name: /Fenix A320 app/ })).getByText('Unknown')).toHaveClass('warning');
    expect(screen.getByText(/could not be read on your computer/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Join session' })).toBeInTheDocument();
  });

  it('a blocking mismatch is red, says what to update, and has no join button', () => {
    show(bridge(pre(rows({ 'aircraft.app_version': ['3.0.1.340', '3.0.0.330', 'reject'] }), false)));
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent("Your setup does not match the captain's");
    expect(within(screen.getByRole('row', { name: /Fenix A320 app/ })).getByText('Different')).toHaveClass('differs');
    expect(screen.getByText(/the captain has app 3.0.0.330, you have 3.0.1.340/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Join session' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
  });
});

describe('session', () => {
  it('both cockpits match: crew, I have control with the sidestick hint, panels and fuel drift', async () => {
    const b = bridge(inSession(), { recent: [{ type: 'action_applied', message: 'CDU 1 INIT pressed by captain. Confirmed.', at: at(40) }] });
    show(b);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Both cockpits match');
    expect(screen.getByText('Last compared 2 seconds ago.')).toBeInTheDocument();
    expect(screen.getByText('Connected, 38 ms')).toBeInTheDocument();
    expect(screen.getByText('First officer (you)')).toBeInTheDocument();
    expect(screen.getByText('Pilot flying')).toBeInTheDocument();
    expect(screen.getByText('Takes over at once. The takeover button on your sidestick does the same.')).toBeInTheDocument();
    expect(screen.getByText('CDU 1 INIT pressed by captain. Confirmed.')).toBeInTheDocument();
    expect(screen.getAllByText('Match')).toHaveLength(3);
    expect(screen.getByText('12 kg apart')).toHaveClass('muted');
    await userEvent.click(screen.getByRole('button', { name: 'I have control' }));
    expect(b.actions.takeControl).toHaveBeenCalledWith(false);
    expect(screen.queryByRole('button', { name: 'You have control' })).toBeNull();
  });

  it('as pilot flying: You have control hands over', async () => {
    const b = bridge(inSession({
      is_pf: true, role: 'pf',
      crew: [{ side: 'fo', you: true, role: 'pf', state: 'connected' }, { side: 'captain', you: false, role: 'pm', state: 'connected' }],
    }));
    show(b);
    await userEvent.click(screen.getByRole('button', { name: 'You have control' }));
    expect(b.actions.giveControl).toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'I have control' })).toBeNull();
  });

  it('a control differs: name, panel, both values, what happened, Set both buttons', async () => {
    const b = bridge(inSession({ desyncs: [BLEED], groups: [{ id: 'overhead', label: 'Overhead', differs: 1 }] }));
    show(b);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('One control differs');
    const block = screen.getByRole('region', { name: 'ENG 1 BLEED' });
    expect(within(block).getByText(/^Overhead, open since/)).toBeInTheDocument();
    expect(within(block).getByText('ON')).toBeInTheDocument();
    expect(within(block).getByText('OFF')).toBeInTheDocument();
    expect(within(block).getByText(/You changed it, but the captain's switch did not move after 3 tries/)).toBeInTheDocument();
    expect(screen.getByText('1 differs')).toHaveClass('differs');
    await userEvent.click(within(block).getByRole('button', { name: 'Set both to OFF' }));
    expect(b.actions.setBoth).toHaveBeenCalledWith('S_OH_PNEUMATIC_ENG1_BLEED', 0);
  });

  it('an indicator that differs has no Set both buttons', () => {
    show(bridge(inSession({
      desyncs: [{ ...BLEED, var: 'I_MIP_GEAR_1_L', label: 'I_MIP_GEAR_1_L', has_label: false, reason: 'observe_mismatch', writable: false, options: [] }],
    })));
    expect(screen.getByRole('heading', { level: 2, name: 'I_MIP_GEAR_1_L' })).toHaveClass('mono');
    expect(screen.queryByRole('button', { name: /Set both/ })).toBeNull();
    expect(screen.getByText(/This is an indicator, so it cannot be set/)).toBeInTheDocument();
  });

  it('after taking control: You have control with the open differences as information', async () => {
    show(bridge(inSession({ is_pf: true, role: 'pf', control_notice: { at: at(3), kind: 'take', groups: ['Main panel'], vars: ['Gear indicator'] } })));
    const notice = screen.getByRole('region', { name: 'You have control' });
    expect(within(notice).getByText(/You took control. Open differences at the handover: Main panel, Gear indicator/)).toBeInTheDocument();
    await userEvent.click(within(notice).getByRole('button', { name: 'OK' }));
    expect(screen.queryByRole('region', { name: 'You have control' })).toBeNull();
  });

  it('peer lost: one line at the top, and the confirm flow for taking control', async () => {
    const b = bridge(inSession({
      peer: 'lost', peer_lost_at: at(12),
      crew: [{ side: 'fo', you: true, role: 'pm', state: 'connected' }, { side: 'captain', you: false, role: 'pf', state: 'lost' }],
    }), { confirmPeerLost: true });
    show(b);
    expect(screen.getByText('The captain lost connection 12 s ago. The session stays open for 5 minutes.')).toBeInTheDocument();
    expect(screen.getByText('Pilot flying, lost connection')).toBeInTheDocument();
    expect(screen.getByText('The captain is not connected. Take control anyway?')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Take control anyway' }));
    expect(b.actions.takeControl).toHaveBeenCalledWith(true);
  });

  it('fuel loaded differently: one amber line, no red, no desync', () => {
    show(bridge(inSession({ owned: [{ var: 'fuel', label: 'Fuel', unit: 'kg', delta: -400, warn: true }] })));
    expect(screen.getByText('Fuel differs by 400 kg. Load the same fuel and payload in the Fenix A320 EFB.')).toHaveClass('warning');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Both cockpits match');
    expect(screen.getByText('400 kg apart')).toHaveClass('muted');
  });

  it('server unreachable: one line, the session view stays', () => {
    show(bridge(inSession({ server: 'reconnecting' })));
    expect(screen.getByText('Connection to the server lost. Reconnecting. Both sims keep flying, just not synced.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Both cockpits match');
    expect(screen.getByText('Reconnecting')).toBeInTheDocument();
  });

  it('several controls differ', () => {
    show(bridge(inSession({ desyncs: [BLEED, { ...BLEED, var: 'X', label: 'APU MASTER' }, { ...BLEED, var: 'Y', label: 'ENG 2 BLEED' }] })));
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Three controls differ');
  });
});

describe('bridge not running', () => {
  it('says so in one line', () => {
    show(bridge(null, { bridgeDown: true }));
    expect(screen.getByText(/The Crosscheck bridge is not running/)).toBeInTheDocument();
  });
});
