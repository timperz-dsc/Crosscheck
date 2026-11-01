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

const TOKENS = new Set(['#111315', '#555a60', '#e3e5e1', '#c9ccc6', '#138a52', '#b42318', '#8a4b00', '#ffffff']);

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

  it('radius 8 px and 44 px buttons', () => {
    const css = all.find((x) => x.f.endsWith('styles.css'))!.text;
    const radii = [...css.matchAll(/border-radius:\s*([^;]+);/g)].map((m) => m[1]!.trim());
    expect(new Set(radii)).toEqual(new Set(['8px']));
    expect(css).toMatch(/\.btn \{[^}]*height: 44px/);
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
