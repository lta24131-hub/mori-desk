// Offline generated pixels only. Does not load Electron or touch the desktop.
import assert from "node:assert/strict";
import fs from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const upstream = process.env.NOOK_ESEARCH_SOURCE || path.resolve(here, "../../../outputs/esearch-15.6.0-source");
const require = createRequire(import.meta.url);
const ts = require(path.join(upstream, "node_modules/typescript/lib/typescript.js"));
const source = fs.readFileSync(path.join(here, "../host/renderer-match.ts"), "utf8");
const javascript = ts.transpileModule(`${source}\nexport {nookFindVerticalOffset};`, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext } }).outputText;
const { nookFindVerticalOffset } = await import(`data:text/javascript;base64,${Buffer.from(javascript).toString("base64")}`);

function image(width, height, offset) {
    const data = new Uint8ClampedArray(width * height * 4);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
        let value = Math.imul(Math.floor(x / 6) + 1, 104729) ^ Math.floor((y + offset) / 3);
        value = Math.imul(value ^ (value >>> 16), 0x45d9f3b);
        value = Math.imul(value ^ (value >>> 16), 0x45d9f3b);
        value = (value ^ (value >>> 16)) >>> 0;
        const p = (y * width + x) * 4;
        data[p] = value & 255; data[p + 1] = (value >>> 8) & 255; data[p + 2] = (value >>> 16) & 255; data[p + 3] = 255;
    }
    return { width, height, data };
}
function measure(label, previous, next, expected) {
    const started = performance.now();
    assert.equal(nookFindVerticalOffset(previous, next)?.dy, -expected);
    const coldMs = performance.now() - started;
    const times = [];
    for (let i = 0; i < 7; i++) {
        const start = performance.now();
        assert.equal(nookFindVerticalOffset(previous, next)?.dy, -expected);
        times.push(performance.now() - start);
    }
    times.sort((a, b) => a - b);
    console.log(JSON.stringify({ label, coldMs: +coldMs.toFixed(2), minMs: +times[0].toFixed(2), medianMs: +times[3].toFixed(2), maxMs: +times[6].toFixed(2), iterations: times.length }));
}
const fixtures = process.env.NOOK_SCROLL_FIXTURES || path.resolve(here, "../../../outputs/scroll-match-fixtures19");
if (fs.existsSync(path.join(fixtures, "frame-96.rgba"))) {
    const load = name => ({ width: 971, height: 525, data: new Uint8ClampedArray(fs.readFileSync(path.join(fixtures, name))) });
    measure("actual WPF 971x525", load("frame-96.rgba"), load("frame-435.44.rgba"), 339);
}
for (const [width, height] of [[971, 525], [1920, 1080], [3840, 2160]]) {
    globalThis.gc?.();
    const shift = Math.floor(height * .65);
    measure(`generated ${width}x${height}`, image(width, height, 0), image(width, height, shift), shift);
}
