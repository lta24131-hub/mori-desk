import assert from "node:assert/strict";
import fs from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import vm from "node:vm";
import { patchRenderer, UPSTREAM_COMMIT } from "../host/prepare.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const upstream = process.env.NOOK_ESEARCH_SOURCE || path.resolve(here, "../../../outputs/esearch-15.6.0-source");
const require = createRequire(import.meta.url);
let ts;
try { ts = require("typescript"); }
catch { ts = require(path.join(upstream, "node_modules/typescript/lib/typescript.js")); }
const original = fs.readFileSync(path.join(upstream, "src/renderer/clip/clip_window.ts"), "utf8");
const adapter = ["renderer-panel.ts", "renderer-schedule.ts", "renderer-session.ts"].map((name) => fs.readFileSync(path.join(here, "../host", name), "utf8")).join("\n");
const patched = patchRenderer(original, adapter);
const parsed = ts.createSourceFile("clip_window.ts", patched, ts.ScriptTarget.Latest, true);
const functions = new Map(parsed.statements.filter(ts.isFunctionDeclaration).map((node) => [node.name?.text, node.getText(parsed)]));

function run(names, globals, extra = "") {
    for (const name of names) assert.ok(functions.has(name), `production function missing: ${name}`);
    const script = names.map((name) => functions.get(name)).join("\n") + "\n" + extra;
    const javascript = ts.transpileModule(script, {
        compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
    }).outputText;
    const context = vm.createContext({ console, ...globals });
    vm.runInContext(javascript, context);
    return context;
}
function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((r, j) => { resolve = r; reject = j; });
    return { promise, resolve, reject };
}

test("adapter is pinned and reuses upstream matching/stitching instead of replacing it", () => {
    assert.equal(UPSTREAM_COMMIT, "6f3f56241cccc8ada2c503df642566322dc67a3e");
    const upstreamParsed = ts.createSourceFile("original.ts", original, ts.ScriptTarget.Latest, true);
    const upstreamMatch = upstreamParsed.statements.find((node) => ts.isFunctionDeclaration(node) && node.name?.text === "longMatch");
    assert.equal(functions.get("longMatch"), upstreamMatch.getText(upstreamParsed).replaceAll("\r\n", "\n"));
    assert.match(functions.get("longMatch"), /await findOffset\(/);
    assert.match(functions.get("addLong"), /await longMatch\(/);
    assert.throws(() => patchRenderer(original.replace("    if (longBusy || longCapped) return;", "/* incompatible upstream */"), adapter), /Upstream drift/);
});

test("busy capture drops concurrent work and clears its guard after a failure", async () => {
    const frame = deferred();
    let captures = 0;
    let additions = 0;
    let failures = 0;
    const context = run(["long_s"], {
        nookDisposed: false, longBusy: false, longCapped: false,
        getNowScreen: () => ({ capture: () => { captures++; return frame.promise; } }),
        nookCaptureFrame: () => { captures++; return frame.promise; },
        addLong: async () => { additions++; },
        nookFail: () => { failures++; },
    });
    const pending = context.long_s();
    await context.long_s();
    assert.equal(captures, 1);
    assert.equal(context.longBusy, true);
    frame.reject(new Error("synthetic frame failure"));
    await pending;
    assert.equal(context.longBusy, false);
    assert.equal(additions, 0);
    assert.equal(failures, 1);
});

test("pointer polling tolerates a terminal null reply and never polls after disposal", () => {
    let reply = null;
    let requests = 0;
    const hitTests = [];
    const sent = [];
    const context = run(["nookUpdatePointer"], {
        nookDisposed: false, longRunning: true, nookPanelDrag: undefined,
        renderSendSync: () => { requests++; return reply; },
        nookIsPanelPoint: (...args) => { hitTests.push(args); return true; },
        nookSetPointerPassthrough: (ignore) => sent.push(["windowIgnoreMouse", [ignore]]),
    });
    assert.doesNotThrow(() => context.nookUpdatePointer(), "host cancellation may win the race before nook-dispose reaches the renderer");
    assert.equal(requests, 1);
    assert.equal(hitTests.length, 0);
    assert.equal(sent.length, 0);
    reply = { po: { x: 80, y: 20 } };
    context.nookUpdatePointer();
    assert.deepEqual(hitTests, [[80, 20]]);
    assert.deepEqual(JSON.parse(JSON.stringify(sent)), [["windowIgnoreMouse", [false]]]);
    context.nookDisposed = true;
    assert.doesNotThrow(() => context.nookUpdatePointer());
    assert.equal(requests, 2, "disposed renderer must not make a late synchronous request");
});

test("cancelled capture never starts another screen request", async () => {
    let captures = 0;
    const context = run(["long_s"], {
        nookDisposed: true, longBusy: false, longCapped: false,
        getNowScreen: () => { captures++; throw new Error("screen must not be touched"); },
        nookCaptureFrame: () => { captures++; throw new Error("screen must not be touched"); },
    });
    await context.long_s();
    assert.equal(captures, 0);
    assert.equal(context.longBusy, false);
});

test("initial selection converts a negative-origin monitor to local physical pixels once", async () => {
    const notifications = [];
    const transforms = [];
    let starts = 0;
    const context = run(["nookInitialize"], {
        nookDisposed: false,
        nookRequest: {
            region: { x: -1800, y: -1050, width: 800, height: 1000 },
            monitor: { x: -1920, y: -1080, width: 1920, height: 1080, scaleFactor: 1.5 },
        },
        mainCanvas: { width: 1920, height: 1080 },
        finalRectFix() {}, setEditorP: (...args) => transforms.push(args),
        drawClipRect() {}, setEditType() {}, startLong: async () => { starts++; },
        nookIPC: { send: (...args) => notifications.push(args) },
    });
    await context.nookInitialize();
    assert.deepEqual(Array.from(context.finalRect), [120, 30, 800, 1000]);
    assert.deepEqual(transforms[0], [1 / 1.5, 0, 0]);
    assert.equal(starts, 1);
    assert.deepEqual(notifications, [["nook-initialized"]]);
});

test("a match resolving after cancellation cannot resurrect the stitched preview", async () => {
    const matching = deferred();
    let put = 0;
    const oldImage = { width: 800, height: 1000 };
    const nextCanvas = { width: 0, height: 0, getContext: () => ({ putImageData() {} }) };
    const context = run(["addLong"], {
        nookDisposed: false, finalRect: [10, 20, 800, 1000], longFX: "y",
        longX: { lastImg: oldImage, lastXY: { x: 0, y: 0 } },
        ele: () => ({ el: nextCanvas }),
        longMatch: () => matching.promise,
        longPutImg: () => { put++; },
    });
    const pending = context.addLong({ width: 1920, height: 1080 });
    context.nookDisposed = true;
    matching.resolve({ clipedImg: { width: 800, height: 300 }, dx: 0, dy: 300, srcDX: 0, srcDY: 300 });
    await pending;
    assert.equal(put, 0);
    assert.equal(context.longX.lastImg, oldImage);
    assert.equal(context.longX.lastXY.y, 0);
});

test("long-image limits reject growth before allocating a new accumulated canvas", () => {
    const oldImage = { width: 8000, height: 8000 };
    const scheduled = [];
    let allocations = 0;
    const context = run(["longPutImg"], {
        nookDisposed: false, longCapped: false, nookLimitReached: false,
        longX: { img: oldImage, imgXY: { x: 0, y: 0 } },
        ele: () => { allocations++; throw new Error("oversized canvas allocation"); },
        setTimeout: (callback) => scheduled.push(callback),
        stopLong: () => {},
    });
    context.longPutImg({ width: 8000, height: 1000 }, 0, 8000);
    assert.equal(allocations, 0);
    assert.equal(context.longCapped, true);
    assert.equal(context.nookLimitReached, true);
    assert.equal(context.longX.img, oldImage);
    assert.equal(scheduled.length, 1);
});

test("upstream accumulated canvas preserves earlier content when scrolling upward", () => {
    const oldImage = { width: 800, height: 1000 };
    const incoming = { width: 800, height: 700 };
    const draws = [];
    const added = [];
    const canvas = { width: 0, height: 0, style: {}, getContext: () => ({ drawImage: (...args) => draws.push(args) }) };
    const context = run(["longPutImg"], {
        nookDisposed: false, longCapped: false, nookLimitReached: false,
        longX: { img: oldImage, imgXY: { x: 0, y: 0 } },
        ele: () => ({ el: canvas }),
        longPreview: { clear() {}, add: (item) => added.push(item) },
        nookRefreshPanel() {},
    });
    context.longPutImg(incoming, 0, -300);
    assert.equal(canvas.width, 800);
    assert.equal(canvas.height, 1300);
    assert.equal(context.longX.imgXY.y, -300);
    assert.deepEqual(draws, [[oldImage, 0, 300], [incoming, 0, 0]]);
    assert.equal(added[0], canvas);
});

test("capture hides rolling controls before sampling and restores them after failure", async () => {
    const tip = { style: { opacity: "1" } };
    const preview = { style: { opacity: "" } };
    let captures = 0;
    const context = run(["nookCaptureFrame"], {
        nookDisposed: false, longRunning: true,
        longTip: { el: { el: tip } }, longPreview: { el: preview },
        nookCaptureOverlays: () => [tip, preview],
        requestAnimationFrame: (callback) => callback(),
        setTimeout: (callback) => callback(),
        getNowScreen: () => ({ capture: async () => {
            captures++;
            assert.equal(tip.style.opacity, "0");
            assert.equal(preview.style.opacity, "0");
            throw new Error("synthetic capture failure");
        } }),
    });
    await assert.rejects(context.nookCaptureFrame(), /synthetic capture failure/);
    assert.equal(captures, 1);
    assert.equal(tip.style.opacity, "1");
    assert.equal(preview.style.opacity, "");
});

test("Escape during the overlay paint delay prevents even one additional screen capture", async () => {
    const callbacks = [];
    let captures = 0;
    const tip = { style: {} };
    const preview = { style: {} };
    const context = run(["nookCaptureFrame"], {
        nookDisposed: false, longRunning: true,
        longTip: { el: { el: tip } }, longPreview: { el: preview },
        nookCaptureOverlays: () => [tip, preview],
        requestAnimationFrame: (callback) => callbacks.push(callback),
        setTimeout: (callback) => callback(),
        getNowScreen: () => { captures++; throw new Error("unexpected capture"); },
    });
    const pending = context.nookCaptureFrame();
    context.nookDisposed = true;
    callbacks.shift()();
    callbacks.shift()();
    assert.equal(await pending, undefined);
    assert.equal(captures, 0);
});

test("mouse wheel cannot pan/zoom the capture editor during scroll, finish, or disposal", () => {
    const wheelNode = parsed.statements.find((node) => node.getText(parsed).startsWith("document.onwheel ="));
    assert.ok(wheelNode);
    let pans = 0;
    let paints = 0;
    const document = { body: { classList: { add: () => { paints++; } } } };
    const context = run([], {
        document, editor: { el: { contains: () => true } },
        nookDisposed: false, nookFinishing: false, longRunning: true,
        nowType: "select", editorP: { zoom: 1, x: 0, y: 0 },
        setEditorP: () => { pans++; },
    }, wheelNode.getText(parsed));
    const wheel = { target: document.body, ctrlKey: false, shiftKey: false, deltaX: 0, deltaY: 120 };
    context.document.onwheel(wheel);
    context.longRunning = false; context.nookFinishing = true;
    context.document.onwheel(wheel);
    context.nookFinishing = false; context.nookDisposed = true;
    context.document.onwheel(wheel);
    assert.equal(pans, 0);
    assert.equal(paints, 0);
    context.nookDisposed = false;
    context.document.onwheel(wheel);
    assert.equal(pans, 1, "completed editor should retain ordinary image panning");
});

test("disposal removes native listeners and releases click-through exactly once", () => {
    const calls = [];
    const context = run(["nookDispose"], {
        nookDisposed: false, nookFinishing: false, longRunning: true,
        uIOhook: { removeAllListeners: () => calls.push("listeners"), stop: () => calls.push("hook") },
        longClipTime: "capture-timer", longMouse: "mouse-timer",
        clearInterval: (timer) => calls.push(timer),
        nookClearScheduledFrame() {}, nookHidePanel() {},
        renderSend: (...args) => calls.push(args),
    });
    context.nookDispose();
    context.nookDispose();
    assert.equal(context.nookDisposed, true);
    assert.equal(context.longRunning, false);
    assert.equal(context.uIOhook, null);
    assert.equal(calls.filter((value) => value === "listeners").length, 1);
    assert.equal(calls.filter((value) => value === "hook").length, 1);
    assert.equal(calls.filter(Array.isArray).length, 1);
    assert.equal(calls.at(-1)[0], "windowIgnoreMouse");
    assert.equal(calls.at(-1)[1][0], false);
});
