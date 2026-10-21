import assert from "node:assert/strict";
import fs from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { patchRenderer } from "../host/prepare.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const upstream = process.env.NOOK_ESEARCH_SOURCE || path.resolve(here, "../../../outputs/esearch-15.6.0-source");
// Reviewed VM packages retain the real pinned engine as a small test dependency,
// while development checkouts may use eSearch's installed package. Production
// Electron bundles the same engine into its renderer; neither path is a mock.
async function phaseEngine() {
    const candidates = [
        path.resolve(here, "../../../scroll-capture/node_modules/picture_match/dist/index.js"),
        path.join(upstream, "node_modules/picture_match/dist/index.js"),
    ];
    const entry = candidates.find(filename => fs.existsSync(filename));
    assert.ok(entry, `The real picture_match 0.1.4 engine is required. Checked: ${candidates.join(", ")}`);
    const manifest = JSON.parse(fs.readFileSync(path.resolve(path.dirname(entry), "../package.json"), "utf8"));
    assert.equal(manifest.name, "picture_match");
    assert.equal(manifest.version, "0.1.4", "phase-aliasing regression requires the pinned real engine");
    return import(pathToFileURL(entry).href);
}
const require = createRequire(import.meta.url);
let ts;
try { ts = require("typescript"); }
catch { ts = require(path.join(upstream, "node_modules/typescript/lib/typescript.js")); }
const source = fs.readFileSync(path.join(here, "../host/renderer-match.ts"), "utf8");
const original = fs.readFileSync(path.join(upstream, "src/renderer/clip/clip_window.ts"), "utf8");
const patched = patchRenderer(original, "");
const parsed = ts.createSourceFile("clip_window.ts", patched, ts.ScriptTarget.Latest, true);
const functions = new Map(parsed.statements.filter(ts.isFunctionDeclaration).map(node => [node.name?.text, node.getText(parsed)]));

// Pixel-only Canvas substitute; no Electron, HWND, desktop capture or input.
class PixelCanvas {
    constructor(width = 0, height = 0) { this._width = width; this._height = height; this.style = {}; this.reset(); }
    reset() { this.data = new Uint8ClampedArray(this.width * this.height * 4); }
    get width() { return this._width; }
    set width(value) { this._width = value; this.reset(); }
    get height() { return this._height; }
    set height(value) { this._height = value; this.reset(); }
    getContext() {
        return {
            drawImage: (image, dx, dy) => {
                assert.equal(dx, Math.round(dx)); assert.equal(dy, Math.round(dy));
                const left = Math.max(0, dx), right = Math.min(this.width, dx + image.width);
                for (let y = Math.max(0, dy); y < Math.min(this.height, dy + image.height); y++) {
                    if (right <= left) continue;
                    const src = ((y - dy) * image.width + left - dx) * 4;
                    this.data.set(image.data.subarray(src, src + (right - left) * 4), (y * this.width + left) * 4);
                }
            },
            putImageData: (image, dx, dy) => this.getContext().drawImage(image, dx, dy),
            getImageData: (x, y, width, height) => {
                const data = new Uint8ClampedArray(width * height * 4);
                for (let row = 0; row < height; row++) {
                    const offset = ((y + row) * this.width + x) * 4;
                    data.set(this.data.subarray(offset, offset + width * 4), row * width * 4);
                }
                return { width, height, data };
            },
        };
    }
}

const script = `
let longFX = "y", nookDisposed = false, longCapped = false, nookLimitReached = false;
let finalRect, longX, warnings = [], refreshes = 0;
let findOffset = async () => { throw new Error("vertical matching must not trust phase correlation"); };
let OffscreenCanvas, ele;
const longPreview = {clear() {}, add() {}};
function nookMatchProblem(message) { warnings.push(message); }
function nookRefreshPanel() { refreshes++; }
${source}
${["longMatch", "addLong", "longPutImg"].map(name => functions.get(name)).join("\n")}
export {nookFindVerticalOffset, nookFindOffset, nookVerticalMatches, nookStaticBands, longMatch, addLong};
export function configure(Canvas, phase, mode = "y") {
  OffscreenCanvas = Canvas; ele = () => ({el: new Canvas()}); longFX = mode;
  if (phase) findOffset = phase;
  nookMatchUnresolved = false; warnings = []; refreshes = 0;
  longX = {lastImg: undefined, lastXY: {x: 0, y: 0}, img: undefined, imgXY: {x: 0, y: 0}};
}
export function setRect(width, height) { finalRect = [0, 0, width, height]; }
export function state() { return {longX, unresolved: nookMatchUnresolved, warnings, refreshes}; }
`;
// Run numeric code in the normal realm. vm wrappers make tight pixel loops
// artificially hundreds of times slower by routing Math through a Proxy.
const javascript = ts.transpileModule(script, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext } }).outputText;
const matcher = await import(`data:text/javascript;base64,${Buffer.from(javascript).toString("base64")}`);

function hash(value) {
    value = Math.imul(value ^ (value >>> 16), 0x45d9f3b);
    value = Math.imul(value ^ (value >>> 16), 0x45d9f3b);
    return (value ^ (value >>> 16)) >>> 0;
}
function frame(offset, width = 971, height = 525, offsetX = 0) {
    const data = new Uint8ClampedArray(width * height * 4);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
        // Textured 6x3 cells simulate a page of small, unique text blocks.
        const value = hash(Math.imul(Math.floor((x + offsetX) / 6) + 1, 104729) ^ Math.floor((y + offset) / 3));
        const p = (y * width + x) * 4;
        data[p] = value & 255; data[p + 1] = (value >>> 8) & 255; data[p + 2] = (value >>> 16) & 255; data[p + 3] = 255;
    }
    return { width, height, data };
}
function canvas(image) {
    const result = new PixelCanvas(image.width, image.height);
    result.data.set(image.data);
    return result;
}
function clone(image) { return { ...image, data: new Uint8ClampedArray(image.data) }; }

function listFrame(offset) {
    const width = 971, height = 525, data = new Uint8ClampedArray(width * height * 4);
    const rows = []; let cursor = 0;
    for (let index = 1; cursor < offset + height; index++) {
        const seed = Math.imul(index * 397 + 103, 2654435761) >>> 0;
        const size = 103 + index * 7 % 19;
        rows.push({ top: cursor, size, seed, index }); cursor += size;
    }
    let current = 0;
    for (let y = 0; y < height; y++) {
        const absolute = y + offset;
        while (current + 1 < rows.length && rows[current + 1].top <= absolute) current++;
        const row = rows[current], localY = absolute - row.top;
        const color = [55 + (row.seed & 127), 45 + ((row.seed >>> 8) & 127), 60 + ((row.seed >>> 16) & 127)];
        for (let x = 0; x < width; x++) {
            let pixel = [246 - row.index % 9, 250 - row.index % 7, 251 - row.index % 11];
            if (localY >= row.size - 3) pixel = [255, 255, 255];
            else if (localY === 0 || localY === row.size - 4 || x === 0 || x === width - 1) pixel = [210, 214, 220];
            else if (x < 86) pixel = color;
            else {
                const line = Math.floor((localY - 10) / 24), inLine = (localY - 10) % 24;
                if (line >= 0 && line < 3 && inLine >= 0 && inLine < 14 && x >= 101) {
                    const words = [
                        `ROW ${String(row.index).padStart(3, "0")} Screen 1 token=${row.seed.toString(16)}`,
                        "Unique paragraph: check every scrolling seam, no missing or repeated lines.",
                        `ROW ${row.index} check=${row.seed % 100003} non-periodic marker`,
                    ];
                    const at = Math.floor((x - 101) / 12), glyphX = Math.floor((x - 101) % 12 / 2), glyphY = Math.floor(inLine / 2);
                    const character = words[line][at];
                    // Deterministic tiny bitmap glyphs need no system font.
                    if (character && character !== " " && glyphX < 5 && (hash(character.charCodeAt(0) * 13 + glyphY) & (1 << glyphX)))
                        pixel = line === 2 ? color : [32, 42, 47];
                }
            }
            const p = (y * width + x) * 4;
            data.set([...pixel, 255], p);
        }
    }
    return { width, height, data };
}

test("full-resolution overlap recovers small, reverse, and more-than-half-screen scrolls", () => {
    const first = frame(500);
    for (const shift of [1, 48, 96, 339, 400, -1, -48, -339, -400]) {
        const match = matcher.nookFindVerticalOffset(first, frame(500 + shift));
        assert.ok(match, `actual ${shift} must have a verified overlap`);
        assert.equal(match.dx, 0);
        assert.equal(match.dy, -shift);
    }
});

test("dimensions, no overlap and ambiguous repeated rows fail closed", () => {
    const first = frame(0);
    assert.equal(matcher.nookFindVerticalOffset(first, frame(1000)), null);
    assert.equal(matcher.nookFindVerticalOffset(first, frame(0, 970)), null);
    assert.equal(matcher.nookFindVerticalOffset(first, { ...first, data: new Uint8ClampedArray(4) }), null);
    const repeated = (offset) => {
        const result = frame(0, 320, 240);
        for (let y = 0; y < result.height; y++) for (let x = 0; x < result.width; x++) {
            const p = (y * result.width + x) * 4;
            const value = hash(Math.floor(x / 5) ^ (((y + offset) % 40) * 104729));
            for (let c = 0; c < 3; c++) result.data[p + c] = (value >>> (c * 8)) & 255;
        }
        return result;
    };
    assert.equal(matcher.nookFindVerticalOffset(repeated(0), repeated(10)), null, "cannot choose which identical row was reached");
});

test("returning exactly to the accepted blank or repeated frame resolves without moving pixels", async () => {
    matcher.configure(PixelCanvas);
    const blank = { width: 320, height: 240, data: new Uint8ClampedArray(320 * 240 * 4).fill(255) };
    assert.deepEqual(await matcher.nookFindOffset(blank, clone(blank)), { dx: 0, dy: 0, confidence: 1 });
    assert.equal(await matcher.nookFindOffset(blank, frame(300, 320, 240)), null);
    assert.equal(matcher.state().unresolved, true);
    assert.deepEqual(await matcher.nookFindOffset(blank, clone(blank)), { dx: 0, dy: 0, confidence: 1 });
    assert.equal(matcher.state().unresolved, false);
    assert.equal(matcher.state().refreshes, 1);
});

test("fixed textured header and footer do not defeat verified page movement", () => {
    const first = frame(0), next = frame(339);
    const header = frame(3000), footer = frame(5000);
    for (const image of [first, next]) {
        image.data.set(header.data.subarray(0, 40 * image.width * 4), 0);
        const start = (image.height - 32) * image.width * 4;
        image.data.set(footer.data.subarray(start), start);
    }
    assert.deepEqual(matcher.nookStaticBands(first, next), { top: 40, bottom: 32 });
    assert.equal(matcher.nookFindVerticalOffset(first, next)?.dy, -339);
});

test("optional XY mode refines and validates a phase proposal instead of trusting its confidence", async () => {
    matcher.configure(PixelCanvas, async () => ({ dx: -38, dy: -50, confidence: 999 }), "xy");
    const first = frame(500, 971, 525, 200), next = frame(548, 971, 525, 239);
    const match = await matcher.nookFindOffset(first, next);
    assert.ok(match);
    assert.equal(match.dx, -39);
    assert.equal(match.dy, -48);
    matcher.configure(PixelCanvas, async () => ({ dx: -400, dy: -400, confidence: 999 }), "xy");
    assert.equal(await matcher.nookFindOffset(first, next), null, "a high bogus confidence must not overwrite the image");
});

test("large verified shifts preserve a real overlap instead of leaving transparent gaps", async () => {
    for (const shift of [339, 400, -400]) {
        matcher.configure(PixelCanvas);
        const first = canvas(frame(500)), next = canvas(frame(500 + shift));
        const match = await matcher.longMatch(first, next);
        assert.equal(match.srcDY, shift);
        const trim = Math.min(175, 525 - Math.abs(shift) - 16);
        assert.equal(match.clipedImg.height, 525 - trim);
        if (shift > 0) assert.equal(match.dy + match.clipedImg.height, shift + 525);
        else assert.equal(match.dy, shift);
        assert.ok(Math.abs(shift) + trim <= 525 - 16, "at least 16 shared rows must survive crop");
    }
});

test("real compositor keeps sequence order, exact pixels and extent across rapid steps and reversal", async () => {
    matcher.configure(PixelCanvas);
    matcher.setRect(971, 525);
    for (const offset of [0, 48, 96, 435, 775, 400]) await matcher.addLong(frame(offset));
    const { longX, unresolved } = matcher.state();
    assert.equal(unresolved, false);
    assert.equal(longX.lastXY.y, 400);
    assert.equal(longX.imgXY.y, 0);
    assert.equal(longX.img.height, 1300);
    assert.deepEqual(longX.img.data, frame(0, 971, 1300).data, "not merely plausible dimensions: every output pixel must be the correct page pixel");
});

test("unmatched frame cannot mutate last accepted image, origin, or accumulated pixels and can recover", async () => {
    matcher.configure(PixelCanvas);
    matcher.setRect(971, 525);
    await matcher.addLong(frame(0));
    await matcher.addLong(frame(48));
    const before = matcher.state().longX;
    const last = before.lastImg, output = before.img, pixels = new Uint8ClampedArray(output.data);
    await matcher.addLong(frame(2000));
    let state = matcher.state();
    assert.equal(state.unresolved, true);
    assert.match(state.warnings.at(-1), /往回滚/);
    assert.equal(state.longX.lastImg, last);
    assert.equal(state.longX.img, output);
    assert.equal(state.longX.lastXY.y, 48);
    assert.deepEqual(state.longX.img.data, pixels);
    await matcher.addLong(frame(96));
    state = matcher.state();
    assert.equal(state.unresolved, false);
    assert.equal(state.longX.lastXY.y, 96);
    assert.equal(state.longX.img.height, 621);
});

test("ordinary repeated card layout catches phase aliasing without optional binary fixtures", async () => {
    const { findOffset } = await phaseEngine();
    const first = listFrame(96), next = listFrame(435);
    const phase = await findOffset(first, next);
    assert.ok(Math.abs(phase.dy + 339) > 100, `old engine must reproduce repeated-row aliasing, got ${phase.dy}`);
    assert.equal(matcher.nookFindVerticalOffset(first, next)?.dy, -339);
    matcher.configure(PixelCanvas);
    matcher.setRect(971, 525);
    for (const offset of [0, 48, 96, 435, 775]) await matcher.addLong(listFrame(offset));
    const { longX, unresolved } = matcher.state();
    assert.equal(unresolved, false);
    assert.equal(longX.img.height, 1300);
    for (const offset of [0, 48, 96, 435, 775]) {
        const expected = listFrame(offset);
        const actual = longX.img.getContext().getImageData(0, offset, 971, 525);
        assert.deepEqual(actual.data, expected.data);
    }
});

// Optional locally generated WPF reference uses the actual desktop test's Row
// factory offscreen. The tests above are always-on and need no binary fixtures.
const fixtures = process.env.NOOK_SCROLL_FIXTURES || path.resolve(here, "../../../outputs/scroll-match-fixtures19");
test("actual WPF subpixel sequence rejects the installed phase engine's wrong repeated rows", { skip: !fs.existsSync(path.join(fixtures, "frame-0.rgba")) }, async () => {
    const { findOffset } = await phaseEngine();
    const frames = [0, 48, 96, 435.44, 774.89].map(offset => ({ width: 971, height: 525, data: new Uint8ClampedArray(fs.readFileSync(path.join(fixtures, `frame-${offset}.rgba`))) }));
    const phase = await findOffset(frames[2], frames[3]);
    assert.ok(Math.abs(phase.dy + 339) > 100, "fixture must reproduce the old large-scroll failure");
    const shifts = frames.slice(1).map((image, index) => -matcher.nookFindVerticalOffset(frames[index], image)?.dy);
    assert.deepEqual(shifts, [48, 48, 339, 340]);
    matcher.configure(PixelCanvas);
    matcher.setRect(971, 525);
    for (const image of frames) await matcher.addLong(image);
    assert.equal(matcher.state().longX.img.height, 1300);
    assert.equal(matcher.state().unresolved, false);
});
