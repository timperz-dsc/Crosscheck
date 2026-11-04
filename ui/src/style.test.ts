import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

// The design rules, checked on the source so they cannot slip in unnoticed.
const SRC = join(__dirname);
const files = (dir: string): string[] =>
  readdirSync(dir).flatMap((f) => {
    const p = join(dir, f);
    return statSync(p).isDirectory() ? files(p) : [p];
  });
const sources = files(SRC).filter((f) => /\.(tsx?|css)$/.test(f) && !/\.test\.tsx?$/.test(f) && !f.includes(`${join('src', 'test')}`));
const all = sources.map((f) => ({ f, text: readFileSync(f, 'utf8') }));

// Light and dark (CRO-39): the brand's ink, paper, greens, plus the dark theme's greys, red and amber.
const LIGHT = { bg: '#ffffff', bar: '#f4f5f3', ink: '#111315', grey: '#555a60', rule: '#e3e5e1', ruleStrong: '#c9ccc6', matches: '#138a52', differs: '#b42318', warning: '#8a4b00' };
const DARK = { bg: '#111315', bar: '#181a1d', ink: '#ffffff', grey: '#a3a8ae', rule: '#2a2d31', ruleStrong: '#3d4146', matches: '#3cb878', differs: '#ff7b72', warning: '#f0b04a' };
const TOKENS = new Set([...Object.values(LIGHT), ...Object.values(DARK)]);

/** WCAG contrast ratio of two #rrggbb colours. */
function contrast(a: string, b: string): number {
  const lum = (hex: string) => {
    const [r, g, bl] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r! + 0.7152 * g! + 0.0722 * bl!;
  };
  const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
  return (hi! + 0.05) / (lo! + 0.05);
}

describe('visual rules', () => {
  it('uses only the design colours', () => {
    for (const { f, text } of all) {
      const hex = text.match(/#[0-9a-fA-F]{6}\b/g) ?? [];
      for (const h of hex) expect(TOKENS.has(h.toLowerCase()), `${f}: ${h}`).toBe(true);
      expect(/rgba?\(|hsla?\(/.test(text), `${f}: rgb/hsl colour`).toBe(false);
    }
  });

  it('has no shadows, gradients or tiny uppercase labels', () => {
    for (const { f, text } of all) {
      expect(/box-shadow|text-shadow|drop-shadow/.test(text), `${f}: shadow`).toBe(false);
      expect(/gradient\(/.test(text), `${f}: gradient`).toBe(false);
      expect(/text-transform:\s*uppercase|textTransform/.test(text), `${f}: uppercase`).toBe(false);
    }
  });

  it('text has good contrast in both themes, on the window and on the status bar', () => {
    for (const t of [LIGHT, DARK]) {
      for (const bg of [t.bg, t.bar]) {
        for (const fg of [t.ink, t.grey, t.differs, t.warning]) expect(contrast(fg, bg), `${fg} on ${bg}`).toBeGreaterThanOrEqual(4.5);
        // Green is only the mark (a graphic): 3:1.
        expect(contrast(t.matches, bg), `${t.matches} on ${bg}`).toBeGreaterThanOrEqual(3);
      }
    }
  });

  it('follows the Windows theme with the same tokens', () => {
    const css = all.find((x) => x.f.endsWith('styles.css'))!.text;
    expect(css).toMatch(/color-scheme: light dark/);
    const dark = css.slice(css.indexOf('@media (prefers-color-scheme: dark)'));
    for (const [k, v] of Object.entries({ bg: DARK.bg, ink: DARK.ink, matches: DARK.matches, differs: DARK.differs, warning: DARK.warning })) {
      expect(dark, k).toContain(v);
    }
    // Components use the variables, never a colour of their own.
    for (const { f, text } of all.filter((x) => !x.f.endsWith('styles.css'))) expect(/#[0-9a-fA-F]{6}\b/.test(text), f).toBe(false);
  });

  it('radius 8 px and 32 px buttons (desktop density)', () => {
    const css = all.find((x) => x.f.endsWith('styles.css'))!.text;
    const radii = [...css.matchAll(/border-radius:\s*([^;]+);/g)].map((m) => m[1]!.trim());
    expect(new Set(radii)).toEqual(new Set(['8px']));
    expect(css).toMatch(/\.btn \{[^}]*height: 32px/);
  });

  it('loads nothing from the network', () => {
    for (const { f, text } of all) {
      expect(/fonts\.googleapis|fonts\.gstatic|https?:\/\/(?!127\.0\.0\.1)/.test(text), `${f}: network URL`).toBe(false);
    }
  });

  it('no emoji', () => {
    for (const { f, text } of all) expect(/\p{Extended_Pictographic}/u.test(text), `${f}: emoji`).toBe(false);
  });
});
