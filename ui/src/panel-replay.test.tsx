// CRO-34: the gate test 2 stream through the UI. The frames come from the bridge's panel builder
// (PanelReplayTests writes ui/src/test/gate-test-2-frames.json from server/test/fixtures/gate-test-2.json).
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { Screens } from './App';
import { pickHeadline } from './panel';
import { bridge, inSession, line } from './test/fixtures';
import framesFile from './test/gate-test-2-frames.json';
import type { GroupRow, Panel } from './types';

interface Frame { t_ms: number; at: string; name: string | null; panel: Panel; groups: GroupRow[] }
const frames = (framesFile as unknown as { frames: Frame[] }).frames;

const RAW_NAME = /\b[A-Z]{1,3}_[A-Z0-9]+_[A-Z0-9_]+\b/;

function status(f: Frame) {
  return inSession({ panel: f.panel, groups: f.groups, last_compared_at: f.at, owned: [] });
}

afterEach(cleanup);

describe('gate test 2 replay through the session panel', () => {
  it('has the moments the screenshots show', () => {
    expect(frames.length).toBeGreaterThan(10);
    expect(frames.map((f) => f.name)).toEqual(expect.arrayContaining(['peak', 'end', 'calm', 'collapsed']));
    expect(frames.find((f) => f.name === 'peak')!.panel.red).toHaveLength(7); // the old panel showed 19
  });

  it.each(frames.map((f) => [f.at.slice(11, 19), f] as const))('%s: at most one line per control, red only for the red lines, no raw names', async (_, f) => {
    render(<Screens bridge={bridge(status(f))} now={new Date(f.at).getTime()} />);
    const red = f.panel.red;
    if (red.length === 0) {
      expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('All controls match');
    } else {
      expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(red[0]!.label);
      if (red.length > 1) {
        expect(screen.getByText(`+${red.length - 1} more`)).toBeInTheDocument();
        await userEvent.click(screen.getAllByRole('button', { name: 'Show' })[0]!);
      }
    }
    // One region per red control, and nothing else red: regions are exactly the red lines.
    const column = document.querySelector<HTMLElement>('.pane-main')!;
    const regions = within(column).queryAllByRole('region').map((r) => r.getAttribute('aria-labelledby') === 'headline-title'
      ? within(r).getByRole('heading', { level: 1 }).textContent : within(r).getByRole('heading', { level: 2 }).textContent);
    expect(regions.sort()).toEqual(red.map((l) => l.label).sort());
    expect(new Set(regions).size).toBe(regions.length);
    // Collapsed lines: one each, with counts.
    if (f.panel.not_synced.count > 0) expect(screen.getAllByText(/^Not synced yet: /)).toHaveLength(1);
    if (f.panel.outside.count > 0) expect(screen.getAllByText(/^Outside the aircraft: /)).toHaveLength(1);
    if (f.panel.indicators.count > 0) expect(screen.getAllByText(/^Indicators differ \(/)).toHaveLength(1);
    // Plain labels everywhere, the log included.
    expect(document.body.textContent).not.toMatch(RAW_NAME);
    // The Panels summary counts the red lines per panel.
    expect(f.groups.reduce((n, g) => n + g.differs, 0)).toBe(red.length);
  });

  it('the headline never jumps across the replay: it changes only when it clears or a higher priority appears', () => {
    let previous: string | null = null;
    const view = render(<Screens bridge={bridge(status(frames[0]!))} now={new Date(frames[0]!.at).getTime()} />);
    for (const f of frames) {
      view.rerender(<Screens bridge={bridge(status(f))} now={new Date(f.at).getTime()} />);
      const h = f.panel.red.length === 0 ? null : screen.getByRole('heading', { level: 1 }).textContent;
      const now = f.panel.red.find((l) => l.label === h) ?? null;
      const before = f.panel.red.find((l) => l.control === previous);
      if (previous !== null && now && now.control !== previous && before) {
        expect(now.priority).toBeLessThan(before.priority);
      }
      previous = now?.control ?? null;
    }
  });
});

describe('pickHeadline', () => {
  const a = line({ control: 'A', priority: 2 });
  const b = line({ control: 'B', priority: 2 });
  const s = line({ control: 'S', priority: 1 });
  it('takes the first line when there is no headline yet', () => expect(pickHeadline(null, [b, a])?.control).toBe('B'));
  it('keeps the current one against an equal or lower priority', () => expect(pickHeadline('A', [b, a])?.control).toBe('A'));
  it('gives way to a higher priority', () => expect(pickHeadline('A', [s, b, a])?.control).toBe('S'));
  it('moves on when it clears', () => expect(pickHeadline('S', [b, a])?.control).toBe('B'));
  it('is empty when nothing is red', () => expect(pickHeadline('A', [])).toBeNull());
});
