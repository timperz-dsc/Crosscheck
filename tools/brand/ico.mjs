// Builds assets/brand/crosscheck.ico (16-256 px, PNG inside) from crosscheck-avatar-dark.svg:
// the mark on a dark rounded tile, so it reads on light and dark backgrounds (an .ico cannot follow the theme).
// Run: npm run ico   (after npm install in tools/brand)
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { Resvg } from "@resvg/resvg-js";

const here = path.dirname(fileURLToPath(import.meta.url));
const brand = path.resolve(here, "../../assets/brand");
const svg = readFileSync(path.join(brand, "crosscheck-avatar-dark.svg"), "utf8");

const sizes = [16, 24, 32, 48, 64, 128, 256];
const pngs = sizes.map((s) => new Resvg(svg, { fitTo: { mode: "width", value: s } }).render().asPng());

const head = Buffer.alloc(6);
head.writeUInt16LE(1, 2); // type: icon
head.writeUInt16LE(sizes.length, 4);
let offset = 6 + 16 * sizes.length;
const entries = sizes.map((s, i) => {
  const e = Buffer.alloc(16);
  e[0] = s === 256 ? 0 : s;
  e[1] = s === 256 ? 0 : s;
  e.writeUInt16LE(1, 4); // planes
  e.writeUInt16LE(32, 6); // bits per pixel
  e.writeUInt32LE(pngs[i].length, 8);
  e.writeUInt32LE(offset, 12);
  offset += pngs[i].length;
  return e;
});
writeFileSync(path.join(brand, "crosscheck.ico"), Buffer.concat([head, ...entries, ...pngs]));
console.log("assets/brand/crosscheck.ico written");
