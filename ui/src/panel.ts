import type { PanelLine } from './types';

/**
 * The headline: the most important red line (priority, then oldest; the bridge sends them in that
 * order). It does not jump: it stays until it clears or a line of higher priority appears.
 */
export function pickHeadline(previous: string | null, lines: PanelLine[]): PanelLine | null {
  if (lines.length === 0) return null;
  const top = lines[0]!;
  const kept = previous === null ? undefined : lines.find((l) => l.control === previous);
  if (kept && kept.priority <= top.priority) return kept;
  return top;
}
