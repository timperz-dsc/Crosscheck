// Rebuilds every file in assets/brand/. Run: npm install && npm run build
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import opentype from "opentype.js";
import { Resvg } from "@resvg/resvg-js";

const here = path.dirname(fileURLToPath(import.meta.url));
const out = path.resolve(here, "../../assets/brand");
mkdirSync(out, { recursive: true });

const fontDir = path.join(here, "node_modules/geist/dist/fonts/geist-sans");
const loadFont = (name) => {
  const b = readFileSync(path.join(fontDir, name));
  return opentype.parse(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength));
};
const semiBold = loadFont("Geist-SemiBold.ttf");
const regular = loadFont("Geist-Regular.ttf");

const INK = "#111315";
const PAPER = "#F4F5F3";
const MUTED = "#555A60";
const THEMES = {
  light: { s1: INK, s2: "#138A52", text: INK, avatarBg: PAPER },
  dark: { s1: "#FFFFFF", s2: "#3CB878", text: "#FFFFFF", avatarBg: INK },
};

const n = (v) => +v.toFixed(3);

// The mark, in its own 100x100 coordinates.
const markStrokes = (t) =>
  `<path d="M20 50 L36 66" stroke="${t.s1}"/><path d="M50 65.5 L82 30" stroke="${t.s2}"/>`;
const markGroup = (t, transform = "") =>
  `<g${transform ? ` transform="${transform}"` : ""} fill="none" stroke-width="12" stroke-linecap="round" stroke-linejoin="round">${markStrokes(t)}</g>`;

const svg = (w, h, body, viewBox = `0 0 ${w} ${h}`, size = true) =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${viewBox}"${size ? ` width="${n(w)}" height="${n(h)}"` : ""}>${body}</svg>\n`;

// Text as outlines. Origin = left of the first glyph's advance box, y = baseline.
function textPath(font, text, x, y, size, letterSpacingEm = 0) {
  const p = font.getPath(text, x, y, size, { letterSpacing: letterSpacingEm });
  return { d: p.toPathData(3), box: p.getBoundingBox() };
}

// Lockup: mark left, wordmark right. Returns body at origin (0,0) plus its size.
function lockup(t, fontSize, { markSize = 1.04 * fontSize, spacing = -0.035 } = {}) {
  const gap = 0.28 * fontSize;
  const scale = fontSize / semiBold.unitsPerEm;
  const cap = semiBold.tables.os2.sCapHeight * scale;
  const xh = semiBold.tables.os2.sxHeight * scale;
  const word = textPath(semiBold, "Crosscheck", 0, 0, fontSize, spacing); // baseline at y=0, ink grows upward
  const wordX = markSize + gap - word.box.x1; // ink starts exactly after the gap
  // Optical centre of the wordmark: halfway between the x-height band and the cap-height band.
  const centre = -(cap + xh) / 4; // negative = above baseline
  const markY = centre - markSize / 2;
  const top = Math.min(word.box.y1, markY);
  const bottom = Math.max(word.box.y2, markY + markSize);
  const width = wordX + word.box.x2;
  const dy = -top;
  const body =
    markGroup(t, `translate(0 ${n(markY + dy)}) scale(${n(markSize / 100)})`) +
    `<path transform="translate(${n(wordX)} ${n(dy)})" d="${word.d}" fill="${t.text}"/>`;
  return { body, width, height: bottom - top, markY: markY + dy, wordX, dy };
}

const writeSvg = (name, content) => writeFileSync(path.join(out, name), content);
const png = (svgText, name, width) => {
  const r = new Resvg(svgText, { fitTo: { mode: "width", value: width }, font: { loadSystemFonts: false } });
  writeFileSync(path.join(out, name), r.render().asPng());
};

for (const [mode, t] of Object.entries(THEMES)) {
  const sfx = mode === "dark" ? "-dark" : "";

  writeSvg(`crosscheck-mark${sfx}.svg`, svg(100, 100, markGroup(t), "0 0 100 100"));

  const avatar = svg(
    100, 100,
    `<rect width="100" height="100" rx="22" fill="${t.avatarBg}"/>${markGroup(t)}`,
    "0 0 100 100",
  );
  writeSvg(`crosscheck-avatar${sfx}.svg`, avatar);
  png(avatar, `crosscheck-avatar${sfx}.png`, 512);

  const l = lockup(t, 100);
  const logo = svg(l.width, l.height, l.body);
  writeSvg(`crosscheck-logo${sfx}.svg`, logo);
  png(logo, `crosscheck-logo${sfx}.png`, 1200);
}

// Social preview, light only.
{
  const t = THEMES.light;
  const W = 1280, H = 640, LEFT = 128, GAP = 44;
  const l = lockup(t, 120, { markSize: 128, spacing: -0.04 });

  const size = 36, lineHeight = size * 1.3;
  const lines = [
    ["Shared cockpit for Microsoft Flight Simulator 2024.", INK],
    ["Every switch is read back and confirmed.", MUTED],
  ];
  const sc = size / regular.unitsPerEm;
  const asc = regular.tables.hhea.ascender * sc;
  const desc = -regular.tables.hhea.descender * sc;

  const blockH = l.height + GAP + lineHeight * lines.length;
  const top = (H - blockH) / 2;

  let body = `<rect width="${W}" height="${H}" fill="${PAPER}"/>`;
  body += `<g transform="translate(${n(LEFT - (20 - 6) * 128 / 100)} ${n(top)})">${l.body}</g>`;
  lines.forEach(([text, fill], i) => {
    const lineTop = top + l.height + GAP + i * lineHeight;
    const baseline = lineTop + (lineHeight - (asc + desc)) / 2 + asc;
    const p = textPath(regular, text, 0, baseline, size, -0.01);
    body += `<path transform="translate(${n(LEFT - p.box.x1)} 0)" d="${p.d}" fill="${fill}"/>`;
  });
  const social = svg(W, H, body);
  png(social, "social-preview.png", W);
}

console.log("assets/brand rebuilt");
