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
const adapter = ["renderer-panel.ts", "renderer-match.ts", "renderer-schedule.ts", "renderer-session.ts"].map((name) => fs.readFileSync(path.join(here, "../host", name), "utf8")).join("\n");
const patched = patchRenderer(original, adapter);
const parsed = ts.createSourceFile("clip_window.ts", patched, ts.ScriptTarget.Latest, true);
const functions = new Map(parsed.statements.filter(ts.isFunctionDeclaration).map((node) => [node.name?.text, node.getText(parsed)]));

function run(names, globals, extra = "") {
    names = [...new Set(["nookAllowsFeature", "nookAllowsOutput", "nookAllowsEditTool", "nookHasAnnotations", ...names])];
    for (const name of names) assert.ok(functions.has(name), `production function missing: ${name}`);
    const script = names.map((name) => functions.get(name)).join("\n") + "\n" + extra;
    const javascript = ts.transpileModule(script, {
        compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
    }).outputText;
    const context = vm.createContext({ console, nookRequest: {}, nookFeaturePolicy: undefined, ...globals });
    vm.runInContext(javascript, context);
    return context;
}
function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((r, j) => { resolve = r; reject = j; });
    return { promise, resolve, reject };
}

test("warm renderer binds feature policy once and rejects output or native start before activation", async () => {
    let queries = 0, removed = 0, applied = 0;
    const request = { features: { pin: false, collect: false, pen: false } };
    const context = run(["nookBindRequest", "nookComplete", "startLong"], {
        nookRequest: undefined, nookDisposed: false,
        nookIPC: { sendSync: () => { queries++; return request; } },
        toolsX: { ding: { el: { el: { remove() { removed++; } } } } },
        nookApplyFeatureTools: () => { applied++; },
    });
    // Missing all pixel/edit/hook fixtures makes any unbound work fail loudly.
    await context.nookComplete("copy");
    await context.startLong();
    assert.equal(context.nookBindRequest(), true);
    assert.equal(context.nookRequest, request);
    assert.equal(context.nookAllowsFeature("pen"), false);
    assert.equal(removed, 1);
    assert.equal(applied, 1);
    assert.equal(context.nookBindRequest(), false);
    assert.equal(queries, 1);
    context.nookDisposed = true;
    context.nookRequest = undefined;
    assert.equal(context.nookBindRequest(), false);
    assert.equal(queries, 1, "late initialization after disposal cannot query or rebind a session");
});

test("patched clip initialization binds identity before loading any native screen implementation", async () => {
    const nativeCalls = [];
    const statement = parsed.statements.find(node => node.getText(parsed).startsWith('renderOn("clip_init"'));
    const callback = statement.expression.arguments[1];
    const first = callback.body.statements.slice(0, 3).map(node => node.getText(parsed)).join("\n");
    const declaration = parsed.statements.find(node => ts.isVariableStatement(node) && node.declarationList.declarations.some(d => d.name.getText(parsed) === "screenShots"));
    assert.equal(declaration.declarationList.declarations[0].initializer, undefined, "module load does not initialize native screenshots");
    const script = ts.transpileModule(`async function activate(imgBuffer) { ${first} }`, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
    const context = vm.createContext({
        nookBindRequest: () => false,
        initScreenShots: () => { nativeCalls.push("load"); return async () => { nativeCalls.push("enumerate"); return {}; }; },
        store: { get: () => undefined }, t: value => value,
    });
    vm.runInContext(script, context);
    assert.deepEqual(nativeCalls, []);
    await context.activate();
    assert.deepEqual(nativeCalls, []);
    context.nookBindRequest = () => true;
    await context.activate();
    assert.deepEqual(nativeCalls, ["load", "enumerate"]);
    assert.equal(patched.includes("window.screen.width"), false, "inactive canvases cannot allocate screen-sized images");
});

test("activation updates small warm canvas and editor to the selected mixed-DPI monitor", async () => {
    const styles = [];
    const frame = { width: 1920, height: 1080 };
    const context = run(["setScreen"], {
        screenShotCache: new Map(), editor: { style: value => styles.push(value) },
        colorSize: 1, colorISize: 1,
        mainCanvas: {}, clipCanvas: {}, drawCanvas: {},
        toCanvas: () => {}, 记忆框选: false, drawClipRect: () => {},
        window: { innerWidth: 1, innerHeight: 1 },
        document: { body: { classList: { add: () => {} } } },
    });
    await context.setScreen({ id: 2, size: { width: 1920, height: 1080 }, scaleFactor: 1.5, capture: async () => ({ toImageData: () => frame }) });
    assert.equal(styles[0].width, "1280px");
    assert.equal(context.mouseBarW, 84, "coordinate hint is recalculated from the activated monitor, not a one-pixel warm viewport");
    for (const canvas of [context.mainCanvas, context.clipCanvas, context.drawCanvas]) {
        assert.equal(canvas.width, 1920); assert.equal(canvas.height, 1080);
    }
    assert.equal(context.nowScreenId, 2);
});

test("disabled annotation categories stop palette and keyboard routes before state mutation", () => {
    const policy = Object.fromEntries(["rectangle", "ellipse", "lines", "pen", "text", "mosaic", "pin", "collect"].map(name => [name, false]));
    const context = run(["setEditType"], {
        nookFeaturePolicy: policy,
        editType: {}, nowType: "select",
        drawMainEls: new Proxy({}, { ownKeys() { assert.fail("disabled tool reached editor work"); } }),
    });
    for (const [main, types] of [["draw", ["free", "eraser", "spray"]], ["filter", ["pixelate", "blur"]], ["shape", ["rect", "mask", "circle", "line", "arrow", "polyline", "polygon", "text", "number"]]]) {
        for (const type of types) {
            assert.equal(context.nookAllowsEditTool(main, type), false);
            context.setEditType(main, type);
        }
    }
    assert.deepEqual(context.editType, {});
    assert.equal(context.nowType, "select");
    assert.equal(context.nookAllowsEditTool("select", "draw"), true);
    assert.equal(context.nookHasAnnotations(), false);
    policy.text = true;
    assert.equal(context.nookAllowsEditTool("shape", "text"), true);
    assert.equal(context.nookAllowsEditTool("shape", "arrow"), false);
});

test("disabled pin and collect never allocate/export result pixels", async () => {
    const context = run(["nookComplete"], {
        nookFeaturePolicy: { pin: false, collect: false },
        nookDisposed: false, nookExporting: false, longRunning: false, nookFinishing: false,
        fabricCanvas: { discardActiveObject() { assert.fail("disabled output reached image work"); } },
    });
    for (const action of ["pin", "collect"]) await context.nookComplete(action);
    assert.equal(context.nookExporting, false);
    assert.equal(context.nookAllowsOutput("copy"), true);
    assert.equal(context.nookAllowsOutput("save"), true);
});

test("expanded palette removes disabled entries and chooses an enabled remembered subtype", () => {
    const control = html => ({ el: { innerHTML: html, hidden: false, style: { setProperty(name, value, priority) { this[name] = [value, priority]; } } } });
    const drawSideEls = { select: { draw: control("select") }, shape: { rect: control("rectangle"), text: control("text") },
        draw: { free: control("pen") }, filter: { pixelate: control("mosaic") } };
    const drawMainEls = Object.fromEntries(Object.keys(drawSideEls).map(key => [key, control(key)]));
    const context = run(["nookApplyFeatureTools"], {
        nookFeaturePolicy: { rectangle: false, text: true, pen: false, mosaic: false },
        drawSideEls, drawMainEls, editType: { select: "draw", shape: "rect", draw: "free", filter: "pixelate" },
    });
    context.nookApplyFeatureTools();
    assert.equal(drawSideEls.shape.rect.el.hidden, true);
    assert.deepEqual(drawSideEls.shape.rect.el.style.display, ["none", "important"]);
    assert.equal(drawSideEls.shape.text.el.hidden, false);
    assert.equal(context.editType.shape, "text", "Shape category cannot target a disabled rectangle on first click");
    assert.equal(drawMainEls.shape.el.innerHTML, "text");
    assert.deepEqual(drawMainEls.draw.el.style.display, ["none", "important"]);
    assert.deepEqual(drawMainEls.filter.el.style.display, ["none", "important"]);
    assert.equal(drawMainEls.select.el.style.display, undefined);
});

test("adapter is pinned and changes only audited matching and overlap clipping in upstream compositor", () => {
    assert.equal(UPSTREAM_COMMIT, "6f3f56241cccc8ada2c503df642566322dc67a3e");
    const upstreamParsed = ts.createSourceFile("original.ts", original, ts.ScriptTarget.Latest, true);
    const upstreamMatch = upstreamParsed.statements.find((node) => ts.isFunctionDeclaration(node) && node.name?.text === "longMatch");
    const expected = upstreamMatch.getText(upstreamParsed).replaceAll("\r\n", "\n")
        .replace("await findOffset(", "await nookFindOffset(")
        .replace("    );\n\n    // 图片应该反过来移动才能拼接上", "    );\n    if (!m) return { dx: 0, dy: 0, srcDX: 0, srcDY: 0, clipedImg: undefined };\n\n    // 图片应该反过来移动才能拼接上")
        .replace('const dw = longFX === "xy" ? clip(img1.width) : 0;', 'const dw = longFX === "xy" ? Math.min(clip(img1.width), Math.max(0, img1.width - Math.abs(dx) - 16)) : 0;')
        .replace("const dh = clip(img1.height);", "const dh = Math.min(clip(img1.height), Math.max(0, img1.height - Math.abs(dy) - 16));");
    assert.equal(functions.get("longMatch"), expected);
    assert.match(functions.get("longMatch"), /await nookFindOffset\(/);
    assert.match(functions.get("addLong"), /await longMatch\(/);
    assert.throws(() => patchRenderer(original.replace("    if (longBusy || longCapped) return;", "/* incompatible upstream */"), adapter), /Upstream drift/);
});

test("busy capture drops concurrent work and clears its guard after a failure", async () => {
    const frame = deferred();
    let captures = 0;
    let additions = 0;
    let failures = 0;
    const context = run(["long_s"], {
        nookDisposed: false, longBusy: false, longCapped: false, nookPanelDrag: undefined,
        nookObserveFrame: () => true,
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

test("unchanged fallback frames bypass matching and a panel drag cannot start capture", async () => {
    let samples = 0;
    let matches = 0;
    let changed = false;
    const context = run(["long_s"], {
        nookDisposed: false, longBusy: false, longCapped: false, nookPanelDrag: {},
        nookCaptureFrame: async () => { samples++; return { width: 4, height: 4 }; },
        nookObserveFrame: () => changed,
        addLong: async () => { matches++; },
        nookFail: error => { throw error; },
    });
    await context.long_s();
    assert.equal(samples, 0);
    context.nookPanelDrag = undefined;
    await context.long_s();
    assert.equal(samples, 1);
    assert.equal(matches, 0);
    changed = true;
    await context.long_s();
    assert.equal(samples, 2);
    assert.equal(matches, 1);
    assert.equal(context.longBusy, false);
});

test("capture wiring observes held scroll keys before keyup and starts fallback exactly once", async () => {
    const listeners = new Map();
    const calls = [];
    const keyNames = ["ArrowDown", "ArrowUp", "PageDown", "PageUp", "Home", "End", "Space"];
    const keys = Object.fromEntries(keyNames.map((name, index) => [name, index + 10]));
    const context = run(["startLong"], {
        nookDisposed: false, nookFinishing: false, longRunning: false, longInited: false,
        finalRect: [0, 0, 800, 600], uIOhook: null,
        initLong: () => { context.longInited = true; context.longRunning = true; calls.push("init"); },
        requestAnimationFrame: callback => callback(), setTimeout: callback => callback(),
        nookWaitForCapturePaint: async () => {},
        long_s: async () => calls.push("first-frame"),
        nookUpdatePointer: () => calls.push("pointer"),
        nookClearScheduledFrame: () => calls.push("clear"),
        nookScheduleFrame: event => calls.push(event),
        nookStartFallbackFrames: () => calls.push("fallback"),
        require: module => {
            assert.equal(module, "uiohook-napi");
            return { UiohookKey: keys, uIOhook: { on: (name, callback) => listeners.set(name, callback), start: () => calls.push("start") } };
        },
    });
    await context.startLong();
    await context.startLong();
    assert.deepEqual(calls, ["init", "first-frame", "pointer", "clear", "start", "fallback"]);
    assert.deepEqual([...listeners.keys()], ["keydown", "keyup", "wheel"]);
    for (const name of keyNames) {
        const event = { keycode: keys[name] };
        listeners.get("keydown")(event);
        listeners.get("keydown")(event);
        assert.deepEqual(calls.slice(-2), [event, event], `${name} repeated keydown must sample without waiting for release`);
    }
    const before = calls.length;
    listeners.get("keydown")({ keycode: 999 });
    assert.equal(calls.length, before, "unrelated typing must not flood capture");
    const wheel = { rotation: 1 };
    listeners.get("wheel")(wheel);
    assert.equal(calls.at(-1), wheel);
});

function stopHarness(result) {
    const events = [];
    const waiter = deferred();
    const context = run(["stopLong"], {
        nookDisposed: false, nookFinishing: false, longRunning: true, nookResultReady: false, nookFinishAction: "copy", nookLimitReached: false,
        longClipTime: undefined,
        nookClearScheduledFrame: () => events.push("clear-schedule"),
        nookFinishingPanel: () => events.push("finishing"),
        clearInterval: () => events.push("clear-interval"),
        nookFinishScrollFrames: () => waiter.promise,
        nookResumePanel: () => events.push("resume"),
        nookStartFallbackFrames: () => events.push("fallback"),
        uIOhook: { removeAllListeners: () => events.push("listeners"), stop: () => events.push("stop-hook") },
        lr: { style: value => events.push(["selection", value]) },
        renderSend: (...value) => events.push(value),
        addLong: async image => { assert.equal(image, undefined); events.push("finalize"); context.longRunning = false; },
        nookPrepareResult: () => { context.nookResultReady = true; events.push("ready"); },
        nookSetExportStatus: (message, busy) => events.push(["partial-status", message, busy]),
        nookRunResultAction: async action => { assert.equal(context.nookFinishing, false); events.push(action); },
        nookHidePanel: () => events.push("hide"), longHide: [{ style: { display: "none" } }],
        followBar: () => events.push("toolbar"), nookFail: error => events.push(["error", error]),
    });
    return { context, events, waiter };
}

test("finish observes a stable tail and finalizes once before direct copy, without opening an editor", async () => {
    const h = stopHarness();
    const pending = h.context.stopLong();
    await h.context.stopLong();
    assert.equal(h.context.nookFinishing, true);
    assert.deepEqual(h.events, ["clear-schedule", "finishing", "clear-interval"]);
    assert.ok(h.context.uIOhook, "native observations cannot stop before stable-tail confirmation");
    h.waiter.resolve(true);
    await pending;
    assert.equal(h.context.uIOhook, null);
    assert.equal(h.events.filter(event => event === "finalize").length, 1);
    assert.ok(h.events.indexOf("listeners") < h.events.indexOf("finalize"));
    assert.ok(h.events.indexOf("finalize") < h.events.indexOf("ready"));
    assert.ok(h.events.indexOf("ready") < h.events.indexOf("copy"));
    assert.equal(h.events.includes("hide") || h.events.includes("toolbar"), false);
    assert.equal(h.context.longHide[0].style.display, "none");
    assert.equal(h.context.longRunning, false);
    assert.equal(h.context.nookFinishing, false);
});

test("unstable finishing resumes the same preview without stopping native hooks or flattening annotations", async () => {
    const h = stopHarness();
    const pending = h.context.stopLong();
    h.waiter.resolve(false);
    await pending;
    assert.deepEqual(h.events, ["clear-schedule", "finishing", "clear-interval", "resume", "fallback"]);
    assert.ok(h.context.uIOhook);
    assert.equal(h.context.longRunning, true);
    assert.equal(h.context.nookFinishing, false);
    assert.equal(h.context.longHide[0].style.display, "none");
});

test("a cap reached before or during finish retains the snapshot without silently copying partial content", async () => {
    for (const action of ["copy", "save", "edit"]) {
        for (const beforeFinish of [false, true]) {
            const h = stopHarness();
            h.context.nookFinishAction = action;
            h.context.nookLimitReached = beforeFinish;
            const pending = h.context.stopLong();
            h.context.nookLimitReached = true;
            h.waiter.resolve(true);
            await pending;
            assert.equal(h.context.nookResultReady, true);
            assert.equal(h.context.longRunning, false);
            assert.equal(h.context.nookFinishing, false);
            assert.equal(h.events.includes("ready"), true);
            assert.match(h.events.find(event => event[0] === "partial-status")?.[1] ?? "", /已保留当前部分/);
            assert.equal(h.events.some(event => ["copy", "save", "edit"].includes(event)), false);
        }
    }
});

test("cancellation while finishing never revives the editor or restarts fallback", async () => {
    const h = stopHarness();
    const pending = h.context.stopLong();
    h.context.nookDisposed = true;
    h.waiter.resolve(true);
    await pending;
    assert.deepEqual(h.events, ["clear-schedule", "finishing", "clear-interval"]);
    assert.equal(h.context.nookFinishing, false);
});

test("native panel uses an independent window without pointer polling or click-through races", () => {
    let requests = 0;
    const hitTests = [];
    const sent = [];
    const context = run(["nookUpdatePointer"], {
        nookDisposed: false, longRunning: true, nookPanelDrag: undefined,
        renderSendSync: () => { requests++; return null; },
        nookIsPanelPoint: (...args) => { hitTests.push(args); return true; },
        nookSetPointerPassthrough: (ignore) => sent.push(["windowIgnoreMouse", [ignore]]),
    });
    assert.doesNotThrow(() => context.nookUpdatePointer());
    assert.equal(requests, 0);
    assert.equal(hitTests.length, 0);
    assert.equal(sent.length, 0);
    context.nookUpdatePointer();
    assert.deepEqual(hitTests, []);
    assert.deepEqual(sent, []);
    context.nookDisposed = true;
    assert.doesNotThrow(() => context.nookUpdatePointer());
    assert.equal(requests, 0, "neither live nor disposed renderer needs synchronous pointer polling");
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

test("an unavailable first accumulated canvas fails without inventing a retained screenshot", () => {
    const scheduled = [];
    const context = run(["longPutImg"], {
        nookDisposed: false, longCapped: false, nookLimitReached: false,
        longX: { img: undefined, imgXY: { x: 0, y: 0 } },
        ele: () => ({ el: { getContext: () => null } }),
        setTimeout: callback => scheduled.push(callback),
    });
    assert.throws(() => context.longPutImg({ width: 200, height: 300 }, 0, 0), /无法创建.*重新截图/);
    assert.equal(context.longX.img, undefined);
    assert.equal(scheduled.length, 0, "a missing first frame cannot enter the retained-result workflow");
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

test("capture leaves all protected surfaces visible even when native sampling fails", async () => {
    const tip = { style: { opacity: "1" } };
    const preview = { style: { opacity: "" } };
    let captures = 0;
    const context = run(["nookCaptureFrame"], {
        nookDisposed: false, longRunning: true,
        longTip: { el: { el: tip } }, longPreview: { el: preview },
        nookCaptureOverlays: () => [tip, preview],
        requestAnimationFrame: (callback) => callback(),
        nookWaitForCapturePaint: () => { throw new Error("unexpected frame paint wait"); },
        setTimeout: () => { throw new Error("unexpected frame timer"); },
        getNowScreen: () => ({ capture: async () => {
            captures++;
            assert.equal(tip.style.opacity, "1");
            assert.equal(preview.style.opacity, "");
            throw new Error("synthetic capture failure");
        } }),
    });
    await assert.rejects(context.nookCaptureFrame(), /synthetic capture failure/);
    assert.equal(captures, 1);
    assert.equal(tip.style.opacity, "1");
    assert.equal(preview.style.opacity, "");
});

test("an already cancelled session cannot begin another native capture", async () => {
    let captures = 0;
    const tip = { style: {} };
    const preview = { style: {} };
    const context = run(["nookCaptureFrame"], {
        nookDisposed: true, longRunning: true,
        longTip: { el: { el: tip } }, longPreview: { el: preview },
        nookCaptureOverlays: () => [tip, preview],
        nookWaitForCapturePaint: () => { throw new Error("unexpected paint wait"); },
        setTimeout: (callback) => callback(),
        getNowScreen: () => { captures++; throw new Error("unexpected capture"); },
    });
    const pending = context.nookCaptureFrame();
    assert.equal(await pending, undefined);
    assert.equal(captures, 0);
});

function stalledPaintHarness() {
    let now = 0, nextId = 0;
    const timers = new Map(), paints = new Map(), captured = [], accepted = [];
    const tip = { style: { opacity: ".8" } };
    const context = run(["nookWaitForCapturePaint", "nookCaptureFrame", "long_s"], {
        nookDisposed: false, longRunning: true, longBusy: false, longCapped: false, nookPanelDrag: undefined,
        nookCaptureOverlays: () => [tip],
        requestAnimationFrame: callback => { const id = ++nextId; paints.set(id, callback); return id; },
        cancelAnimationFrame: id => paints.delete(id),
        setTimeout: (callback, delay) => { const id = ++nextId; timers.set(id, { callback, at: now + delay }); return id; },
        clearTimeout: id => timers.delete(id),
        getNowScreen: () => ({ capture: async () => {
            assert.equal(tip.style.opacity, ".8");
            captured.push(now);
            return { toImageData: () => ({ width: 4, height: 4, serial: captured.length }) };
        } }),
        nookObserveFrame: () => true,
        addLong: async image => accepted.push(image.serial),
        nookFail: error => { throw error; },
    });
    async function flush() { for (let i = 0; i < 10; i++) await Promise.resolve(); }
    async function advance(delay) {
        const end = now + delay;
        for (;;) {
            const entry = [...timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
            if (!entry) break;
            const [id, timer] = entry; timers.delete(id); now = timer.at; timer.callback(); await flush();
        }
        now = end; await flush();
    }
    async function paint() {
        const [id, callback] = paints.entries().next().value;
        paints.delete(id); callback(now); await flush();
    }
    return { context, paints, timers, captured, accepted, tip, advance, paint, flush };
}

test("only initial paint settling has a bounded deadline when the renderer stops painting", async () => {
    assert.match(functions.get("startLong"), /await nookWaitForCapturePaint\(\)/, "startup must use the same bounded painting path");
    for (const paintCount of [0, 1]) {
        const h = stalledPaintHarness();
        for (let frame = 0; frame < 2; frame++) {
            const pending = h.context.nookWaitForCapturePaint();
            if (paintCount) await h.paint();
            const stalePaint = [...h.paints.values()][0];
            await h.advance(79);
            assert.equal(h.captured.length, 0);
            await h.advance(1);
            await pending;
            assert.equal(h.captured.length, 0, "startup settle is separate from native sampling");
            assert.equal(h.tip.style.opacity, ".8");
            assert.equal(h.paints.size, 0); assert.equal(h.timers.size, 0);
            stalePaint(0); await h.flush();
            assert.equal(h.paints.size, 0, "a late first paint cannot schedule another paint after the timeout won");
        }
        assert.deepEqual(h.accepted, []);
    }
});

test("initial painting takes the fast path and clears its fallback deadline", async () => {
    const h = stalledPaintHarness();
    const pending = h.context.nookWaitForCapturePaint();
    await h.paint(); await h.paint();
    await pending;
    assert.deepEqual(h.captured, []);
    assert.equal(h.context.longBusy, false);
    assert.equal(h.paints.size, 0); assert.equal(h.timers.size, 0);
    await h.advance(1000);
    assert.deepEqual(h.accepted, [], "expired paint deadline cannot create a native capture");
});

test("repeated native frames have no paint or timer dependency after startup", async () => {
    const h = stalledPaintHarness();
    for (let i = 0; i < 20; i++) await h.context.long_s();
    assert.equal(h.captured.length, 20);
    assert.ok(h.captured.every(time => time === 0), "no artificial per-frame delay");
    assert.equal(h.paints.size, 0); assert.equal(h.timers.size, 0);
    assert.equal(h.context.longBusy, false);
    assert.equal(h.tip.style.opacity, ".8");
});

test("Escape during native capture releases its guard without accepting late pixels", async () => {
    const h = stalledPaintHarness();
    const native = deferred();
    h.context.getNowScreen = () => ({ capture: () => native.promise });
    const pending = h.context.long_s();
    h.context.nookDisposed = true;
    native.resolve({ toImageData() { throw new Error("late pixels must be discarded"); } });
    await pending;
    assert.deepEqual(h.captured, []);
    assert.deepEqual(h.accepted, []);
    assert.equal(h.context.longBusy, false);
    assert.equal(h.paints.size, 0); assert.equal(h.timers.size, 0);
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

test("completed image wheel browses under every drawing tool without changing a stroke", () => {
    const wheelNode = parsed.statements.find((node) => node.getText(parsed).startsWith("document.onwheel ="));
    const transforms = [];
    const context = run([], {
        document: { body: { classList: { add() {} } } }, editor: { el: { contains: () => true } },
        nookDisposed: false, nookFinishing: false, longRunning: false,
        editorP: { zoom: .5, x: 72, y: 52 },
        strokeWidthEl: { gv: 3 }, strokeWidthF: { set: () => assert.fail("wheel changed the pen") },
        setEditorP: (...args) => transforms.push(args),
    }, wheelNode.getText(parsed));
    for (const nowType of ["draw", "shape", "select"]) {
        context.nowType = nowType;
        context.document.onwheel({ target: {}, ctrlKey: false, shiftKey: false, deltaX: 0, deltaY: 120 });
        assert.deepEqual(transforms.at(-1), [.5, 72, -188]);
    }
});

test("first result zoom uses fitted size and current pointer, even before a mousemove", () => {
    const wheelNode = parsed.statements.find((node) => node.getText(parsed).startsWith("document.onwheel ="));
    const transforms = [];
    const context = run([], {
        document: { body: { classList: { add() {} } } }, editor: { el: { contains: () => true } },
        nookDisposed: false, nookFinishing: false, longRunning: false,
        editorP: { zoom: .5, x: 100, y: 80 }, mainCanvas: { width: 1000 }, zoomW: 999999,
        setEditorP: (...args) => transforms.push(args),
    }, wheelNode.getText(parsed));
    context.document.onwheel({ target: {}, ctrlKey: true, deltaY: -300, clientX: 250, clientY: 200 });
    assert.deepEqual(transforms[0], [1, -150, -120]);
    assert.equal(context.zoomW, 1000);
    context.editorP.zoom = 8;
    context.document.onwheel({ target: {}, ctrlKey: true, deltaY: -300, clientX: 250, clientY: 200 });
    assert.equal(transforms.at(-1)[0], 8);
});

test("result image stays centered when narrow and cannot be scrolled entirely out of view", () => {
    const context = run(["nookConstrainResultPosition"], {
        mainCanvas: { width: 1000, height: 5000 }, window: { innerWidth: 800, innerHeight: 600 },
        nookResultScroll: { scrollTop: 0 }, nookResultScrollContent: { style: {} },
    });
    let p = context.nookConstrainResultPosition(.5, -999999, 999999);
    assert.equal(p.x, 284);
    assert.equal(p.y, 112);
    assert.equal(context.nookResultScroll.scrollTop, 0);
    assert.equal(context.nookResultScrollContent.style.height, "2536px");
    p = context.nookConstrainResultPosition(.5, 999999, -999999);
    assert.equal(p.x, 284);
    assert.equal(p.y, -3848);
    assert.equal(context.nookResultScroll.scrollTop, 1980);
    p = context.nookConstrainResultPosition(2, -999999, 0);
    assert.equal(p.x, -620);
    p = context.nookConstrainResultPosition(2, 999999, 0);
    assert.equal(p.x, 12);
    context.mainCanvas.height = 200;
    p = context.nookConstrainResultPosition(.5, -999999, -999999);
    assert.equal(p.y, 112, "short image cannot drift above header");
    assert.equal(context.nookResultScroll.scrollTop, 0);
    assert.equal(context.nookResultScrollContent.style.height, "556px");
});
