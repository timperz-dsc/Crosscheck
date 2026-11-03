import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { Screens } from './App';
import { BLEED, NOW, at, bridge, idle, inSession, line, panel } from './test/fixtures';
import type { PanelItem, Preflight } from './types';

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

  it('saves a new session server address and shows the log folder', async () => {
    const user = userEvent.setup();
    const b = bridge(idle());
    show(b);
    expect(screen.getByText('C:\Users\pilot\AppData\Local\Crosscheck\logs')).toBeInTheDocument();
    const save = screen.getByRole('button', { name: 'Save' });
    expect(save).toBeDisabled();
    const input = screen.getByLabelText('Session server');
    await user.clear(input);
    await user.type(input, 'crosscheck.up.railway.app');
    await user.click(save);
    expect(b.actions.setServer).toHaveBeenCalledWith('crosscheck.up.railway.app');
  });

  it('shows why a request failed', () => {
    show(bridge(idle({ last_error: 'No session with the code ZZZZZZ. Check the code with the captain.' })));
    expect(screen.getByText('No session with the code ZZZZZZ. Check the code with the captain.')).toHaveClass('warning');
  });
});

describe('session ended', () => {
  it('shows where the log of this computer is', () => {
    show(bridge(inSession({ phase: 'ended' })));
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('The session has ended');
    expect(screen.getByText('C:\Users\pilot\AppData\Local\Crosscheck\logs')).toBeInTheDocument();
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
      ['bridge_version', 'Crosscheck bridge', '0.3.0', '0.3.0'],
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
  it('all controls match: one calm line, crew, I have control with the sidestick hint, panels, log and fuel drift', async () => {
    const b = bridge(inSession({ panel: panel({ log: [{ type: 'action_applied', text: 'CDU 1 INIT pressed by captain. Confirmed.', at: at(40) }] }) }));
    show(b);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('All controls match');
    expect(screen.getByText('Last compared 2 seconds ago.')).toBeInTheDocument();
    expect(screen.getByText('Connected, 38 ms')).toBeInTheDocument();
    expect(screen.getByText('First officer (you)')).toBeInTheDocument();
    expect(screen.getByText('Pilot flying')).toBeInTheDocument();
    expect(screen.getByText('Takes over at once. The takeover button on your sidestick does the same.')).toBeInTheDocument();
    // The log is always visible in the right column, next to the panels.
    const log = screen.getByRole('list', { name: 'Log' });
    expect(within(log).getByText('CDU 1 INIT pressed by captain. Confirmed.')).toBeInTheDocument();
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

  it('one red line: the headline says what differs and offers its action, Set both', async () => {
    const b = bridge(inSession({ panel: panel({ red: [BLEED] }), groups: [{ id: 'overhead', label: 'Overhead', differs: 1 }] }));
    show(b);
    const headline = screen.getByRole('region', { name: 'ENG 1 BLEED' });
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('ENG 1 BLEED');
    expect(within(headline).getByText('You ON, the captain OFF.')).toBeInTheDocument();
    expect(within(headline).queryByText(/more/)).toBeNull();
    expect(screen.getByText('1 differs')).toHaveClass('differs');
    expect(within(headline).getByRole('button', { name: 'Set both to ON' })).toHaveClass('btn-primary');
    await userEvent.click(within(headline).getByRole('button', { name: 'Set both to OFF' }));
    expect(b.actions.setBoth).toHaveBeenCalledWith('S_OH_PNEUMATIC_ENG1_BLEED', 0);
  });

  it('several red lines: the most important one on top with +N more; Show opens one line per control', async () => {
    const stuck = line({
      control: 'S_OH_APU_AUTOEXTING_RESET', label: 'APU AUTO EXTING RESET', priority: 1, kind: 'release', where: 'here',
      action: { type: 'release', var: 'S_OH_APU_AUTOEXTING_RESET' }, reason: 'stuck_pressed',
    });
    const alt = line({
      control: 'E_FCU_ALTITUDE', label: 'FCU ALT', group: 'fcu', group_label: 'FCU', priority: 2, kind: 'turn',
      you: { value: 100, text: '100' }, other: { value: 1000, text: '1000' }, reason: 'display_mismatch',
    });
    const ls = line({
      control: 'S_FCU_EFIS1_LS', label: 'LS (Capt)', group: 'efis', group_label: 'EFIS', priority: 2, kind: 'lamp', where: 'here',
      action: { type: 'press_here', var: 'S_FCU_EFIS1_LS', label: 'LS (Capt)' }, reason: 'toggle_mismatch',
    });
    const b = bridge(inSession({ panel: panel({ red: [stuck, alt, ls] }) }));
    show(b);
    const headline = screen.getByRole('region', { name: 'APU AUTO EXTING RESET' });
    expect(within(headline).getByText('Stuck pressed in your cockpit.')).toBeInTheDocument();
    expect(within(headline).getByText('+2 more')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'FCU ALT' })).toBeNull(); // collapsed by default
    await userEvent.click(within(headline).getByRole('button', { name: 'Release it' }));
    expect(b.actions.releaseIt).toHaveBeenCalledWith('S_OH_APU_AUTOEXTING_RESET');

    await userEvent.click(within(headline).getByRole('button', { name: 'Show' }));
    const altLine = screen.getByRole('region', { name: 'FCU ALT' });
    expect(within(altLine).getByText(/^FCU, since/)).toBeInTheDocument();
    expect(within(altLine).getByText('Turn FCU ALT in the cockpit that is wrong.')).toBeInTheDocument();
    expect(within(altLine).getByText('100')).toBeInTheDocument();
    expect(within(altLine).queryByRole('button')).toBeNull(); // never turned automatically, no button
    const lsLine = screen.getByRole('region', { name: 'LS (Capt)' });
    expect(within(lsLine).getByText('Lit here only.')).toBeInTheDocument();
    await userEvent.click(within(lsLine).getByRole('button', { name: 'Press LS (Capt) here only' }));
    expect(b.actions.pressHere).toHaveBeenCalledWith('S_FCU_EFIS1_LS');
    // Order: headline, then the others as the bridge sent them (priority, then oldest).
    expect(altLine.compareDocumentPosition(lsLine) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('the headline does not jump: a new lower or equal priority line stays behind +N more', () => {
    const a = line({ control: 'A', label: 'LS (Capt)', priority: 2, kind: 'lamp', where: 'here' });
    const older = line({ control: 'B', label: 'WR TILT', priority: 2, kind: 'by_hand', since: at(60) });
    const safety = line({ control: 'C', label: 'IDG 1', priority: 1, kind: 'not_mirrored', where: 'there' });
    const view = show(bridge(inSession({ panel: panel({ red: [a] }) })));
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('LS (Capt)');
    // An older line of the same priority arrives (it sorts first): the headline stays.
    view.rerender(<Screens bridge={bridge(inSession({ panel: panel({ red: [older, a] }) }))} now={NOW} />);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('LS (Capt)');
    // A safety line: it takes the headline.
    view.rerender(<Screens bridge={bridge(inSession({ panel: panel({ red: [safety, older, a] }) }))} now={NOW} />);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('IDG 1');
    expect(screen.getByText("Pressed in the captain's cockpit only.")).toBeInTheDocument();
    // It clears: the next most important line.
    view.rerender(<Screens bridge={bridge(inSession({ panel: panel({ red: [older, a] }) }))} now={NOW} />);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('WR TILT');
  });

  it('every kind says what to do (gate test 2 section 4 templates)', async () => {
    const kinds = [
      line({ control: 'K1', label: 'IDG 1', kind: 'not_mirrored', where: 'there', priority: 1 }),
      line({ control: 'K2', label: 'FLAPS', kind: 'by_hand' }),
      line({ control: 'K3', label: 'APU AUTO EXTING RESET', kind: 'release', where: 'there' }),
      line({ control: 'K4', label: 'FD (Capt)', kind: 'press_lost', where: 'there' }),
      line({ control: 'K5', label: 'FCU HDG', kind: 'mode', message: 'HDG managed differs between the cockpits – push or pull the knob in the cockpit that is wrong.' }),
      line({ control: 'K6', label: 'ENG 1 FIRE TEST', kind: 'held', where: 'here' }),
      line({ control: 'K7', label: 'SVCE INT OVRD', kind: 'released', where: 'here' }),
    ];
    show(bridge(inSession({ panel: panel({ red: kinds }) })));
    await userEvent.click(screen.getByRole('button', { name: 'Show' }));
    const region = (name: string) => screen.getByRole('region', { name });
    // The headline shows its full sentence when the list is open.
    expect(within(region('IDG 1')).getByText('Not mirrored (irreversible). Do the same in your cockpit if intended.')).toBeInTheDocument();
    expect(within(region('FLAPS')).getByText('Set it by hand in both cockpits.')).toBeInTheDocument();
    expect(within(region('APU AUTO EXTING RESET')).getByText('The captain can release it there.')).toBeInTheDocument();
    expect(within(region('APU AUTO EXTING RESET')).queryByRole('button', { name: 'Release it' })).toBeNull();
    expect(within(region('FD (Capt)')).getByText('Press FD (Capt) in the cockpit that is wrong.')).toBeInTheDocument();
    expect(within(region('FCU HDG')).getByText(/push or pull the knob in the cockpit that is wrong/)).toBeInTheDocument();
    expect(within(region('ENG 1 FIRE TEST')).getByText('Held down in your cockpit only.')).toBeInTheDocument();
    expect(within(region('SVCE INT OVRD')).getByText('The release never arrived there. Check it in both cockpits.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Set both|here only|Release it/ })).toBeNull();
  });

  it('after taking control: You have control with the open differences as information', async () => {
    show(bridge(inSession({ is_pf: true, role: 'pf', control_notice: { at: at(3), kind: 'take', groups: ['Main panel'], vars: ['Gear indicator'] } })));
    const notice = screen.getByRole('region', { name: 'You have control' });
    expect(within(notice).getByText(/You took control. Open differences at the handover: Main panel, Gear indicator/)).toBeInTheDocument();
    await userEvent.click(within(notice).getByRole('button', { name: 'OK' }));
    expect(screen.queryByRole('region', { name: 'You have control' })).toBeNull();
  });

  it('peer lost: one line, and the confirm flow for taking control', async () => {
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

  it('fuel loaded differently: one amber line, no red', () => {
    show(bridge(inSession({ owned: [{ var: 'fuel', label: 'Fuel', unit: 'kg', delta: -400, warn: true }] })));
    expect(screen.getByText('Fuel differs by 400 kg – load the same fuel in the EFB.')).toHaveClass('warning');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('All controls match');
    expect(screen.getByText('400 kg apart')).toHaveClass('muted');
  });

  it('server unreachable: one line, the session view stays', () => {
    show(bridge(inSession({ server: 'reconnecting' })));
    expect(screen.getByText('Connection to the server lost. Reconnecting. Both sims keep flying, just not synced.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('All controls match');
    expect(screen.getByText('Reconnecting')).toBeInTheDocument();
  });
});

describe('bridge not running', () => {
  it('says so in one line', () => {
    show(bridge(null, { bridgeDown: true }));
    expect(screen.getByText(/The Crosscheck bridge is not running/)).toBeInTheDocument();
  });
});

describe('session: everything else collapsed under the red lines (CRO-34)', () => {
  const item = (label: string, over: Partial<PanelItem> = {}): PanelItem => ({ control: label, label, ...over });

  it('red first, then amber lines, then the grey indicators line', () => {
    show(bridge(inSession({
      panel: panel({
        red: [BLEED],
        not_synced: { count: 1, items: [item('AP1', { mine: true, who: 'you', at: at(5) })] },
        indicators: { count: 1, items: [item('Gear indicator', { you: { value: 1, text: '1' }, other: { value: 0, text: '0' }, since: at(30) })] },
      }),
      owned: [{ var: 'fuel', label: 'Left tank fuel', unit: 'kg', delta: -400, warn: true }],
    })));
    const red = screen.getByRole('region', { name: 'ENG 1 BLEED' });
    const amber = screen.getByText('Not synced yet: 1 control');
    const fuel = screen.getByText('Fuel differs by 400 kg – load the same fuel in the EFB.');
    const quiet = screen.getByText('Indicators differ (1)');
    expect(amber).toHaveClass('warning');
    expect(fuel).toHaveClass('warning');
    expect(quiet).toHaveClass('muted');
    expect(red.compareDocumentPosition(amber) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(amber.compareDocumentPosition(quiet) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('not synced yet: one amber line with a count, details on click', async () => {
    const names = ['AP1', 'AP2', 'EXPED', 'ENG MODE'];
    show(bridge(inSession({
      panel: panel({ not_synced: { count: 4, items: names.map((n, i) => item(n, { mine: i % 2 === 0, who: i % 2 === 0 ? 'you' : 'captain', at: at(i) })) } }),
    })));
    expect(screen.getByText('Not synced yet: 4 controls')).toHaveClass('warning');
    expect(screen.queryByText('AP1')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Show' }));
    const list = screen.getByRole('list', { name: 'Not synced yet' });
    expect(within(list).getAllByRole('listitem').map((li) => li.firstChild?.textContent)).toEqual(names);
    expect(within(list).getAllByText(/^moved by (you|the captain), /)).toHaveLength(4);
  });

  it('walkaround items are one amber "outside" line, never red', async () => {
    show(bridge(inSession({
      panel: panel({ outside: { count: 2, items: [item('EXT CARGO BAY AFT LT'), item('EXT MAINT BUS')] } }),
    })));
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('All controls match');
    expect(screen.getByText('Outside the aircraft: 2 items differ')).toHaveClass('warning');
    await userEvent.click(screen.getByRole('button', { name: 'Show' }));
    expect(within(screen.getByRole('list', { name: 'Outside the aircraft' })).getByText('EXT MAINT BUS')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'EXT MAINT BUS' })).toBeNull();
  });

  it('indicators are one grey line; details on click; never red', async () => {
    show(bridge(inSession({
      panel: panel({ indicators: { count: 2, items: [
        item('Gear indicator', { you: { value: 1, text: '1' }, other: { value: 0, text: '0' }, since: at(30) }),
        item('VOLT BAT 1 display', { you: { value: 26, text: '26' }, other: { value: 27.8, text: '27.8' }, since: at(30) }),
      ] } }),
    })));
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('All controls match');
    await userEvent.click(screen.getByRole('button', { name: 'Show' }));
    const list = screen.getByRole('list', { name: 'Indicators that differ' });
    expect(within(list).getByText('Gear indicator')).toBeInTheDocument();
    expect(within(list).getAllByText(/you .*, captain .*, since/)).toHaveLength(2);
    expect(screen.queryByRole('region', { name: 'Gear indicator' })).toBeNull();
  });

  it('hardware fighting, a counter suspect and a disconnected sim are amber lines of their own', () => {
    show(bridge(inSession({
      panel: panel({
        amber: [
          { key: 'hardware_fighting:S_MIP_PARKING_BRAKE', kind: 'hardware_fighting', label: 'PARKING BRAKE', mine: false, who: 'captain', at: at(3) },
          { key: 'counter_suspect:S_X', kind: 'counter_suspect', label: 'DOOR VIDEO', mine: false, who: 'captain', at: at(4) },
          { key: 'sim_disconnected:host', kind: 'sim_disconnected', label: null, mine: false, who: 'captain', at: at(5) },
        ],
      }),
    })));
    expect(screen.getByText(/Hardware fighting: PARKING BRAKE keeps switching back and forth between the two cockpits/)).toHaveClass('warning');
    expect(screen.getByText(/DOOR VIDEO looks like a press counter/)).toHaveClass('warning');
    expect(screen.getByText("The captain's simulator is not connected. Panels are not compared until it is back.")).toHaveClass('warning');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('All controls match');
  });
});

describe('update notice', () => {
  const update = (state: 'none' | 'checking' | 'ready' | 'error', reason: 'unsupported_version' | null = null) =>
    idle({ update: { state, version: state === 'ready' ? '0.4.0' : null, reason } });

  it('shows nothing when not installed or nothing is new', () => {
    show(bridge(idle()));
    expect(screen.queryByRole('status')).toBeNull();
    show(bridge(update('none')));
    expect(screen.queryByText(/Update ready/)).toBeNull();
  });

  it('offers a restart when an update is ready', async () => {
    const b = bridge(update('ready'));
    show(b);
    expect(screen.getByRole('status')).toHaveTextContent('Update ready (0.4.0) – restart to install.');
    await userEvent.click(screen.getByRole('button', { name: 'Restart and update' }));
    expect(b.actions.applyUpdate).toHaveBeenCalled();
  });

  it('says so when the server refused our version and an update is being looked for', () => {
    show(bridge(update('checking', 'unsupported_version')));
    expect(screen.getByRole('status')).toHaveTextContent('out of date. Looking for an update');
  });

  it('tells the pilot where to download when no update was found', () => {
    show(bridge(update('error', 'unsupported_version')));
    expect(screen.getByRole('status')).toHaveTextContent('no update could be found');
  });
});
