import assert from "node:assert/strict";
import fs from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
let ts;
try { ts = require("typescript"); }
catch {
    const upstream = process.env.NOOK_ESEARCH_SOURCE || path.resolve(here, "../../../outputs/esearch-15.6.0-source");
    ts = require(path.join(upstream, "node_modules/typescript/lib/typescript.js"));
}
const panelSource = fs.readFileSync(path.join(here, "../host/renderer-panel.ts"), "utf8");
const sessionSource = fs.readFileSync(path.join(here, "../host/renderer-session.ts"), "utf8");
const sessionAst = ts.createSourceFile("renderer-session.ts", sessionSource, ts.ScriptTarget.Latest, true);
const sessionFunctions = new Map(sessionAst.statements.filter(ts.isFunctionDeclaration).map(node => [node.name?.text, node.getText(sessionAst)]));
const keydownWiring = sessionAst.statements.find(node => ts.isExpressionStatement(node) &&
    ts.isCallExpression(node.expression) && node.expression.expression.getText(sessionAst) === "window.addEventListener" &&
    node.expression.arguments[0]?.text === "keydown" && node.expression.arguments[1]?.getText(sessionAst) === "nookCaptureKeydown");
assert.ok(keydownWiring, "production keydown capture registration missing");
function compile(source) {
    return ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;
}

// Execute production panel and session functions. Only DOM pixel encoding,
// IPC transport and screen sampling are fakes; no native window is reachable.
// The real independent panel document/host path is covered in host-lifecycle.
function harness(options = {}) {
    const body = { children: [] };
    const preview = { hidden: false, style: {} };
    const tip = { style: {} };
    const finish = {};
    const ipc = [], sent = [], cleanup = [], canvases = [], requests = [];
    const handlers = new Map(), keys = new Map();
    let mouse = { po: { x: 400, y: 300 }, overPanel: false };
    let queries = 0, finishes = 0, captures = 0;
    const context = vm.createContext({
        console,
        document: { body, createElement(tag) {
            if (tag !== "canvas") return { style: {} };
            const canvas = { width: 0, height: 0, draws: [], getContext: () => options.noContext ? null : { drawImage: (...args) => canvas.draws.push(args) }, toDataURL: type => { assert.equal(type, "image/png"); return "data:image/png;base64,AA=="; } };
            canvases.push(canvas); return canvas;
        } },
        window: { innerWidth: options.width ?? 1280, innerHeight: options.height ?? 800, addEventListener: (name, fn, capture) => keys.set(name, { fn, capture }) },
        longPreview: { el: preview }, finishLongB: finish, longTip: { el: { el: tip } },
        nookDisposed: false, nookFinishing: false, nookExporting: false, nookResultReady: false, nookResultMode: false, nookMatchUnresolved: false, longRunning: true,
        longX: {}, finalRect: options.rect ?? [100, 100, 800, 600], ratio: options.ratio ?? 1,
        longClipTime: "capture-interval", longMouse: null,
        uIOhook: { removeAllListeners: () => cleanup.push("listeners"), stop: () => cleanup.push("hook") },
        clearInterval: value => cleanup.push(value), nookClearScheduledFrame: () => cleanup.push("scheduled-frame"),
        renderSend: (...args) => sent.push(JSON.parse(JSON.stringify(args))),
        renderSendSync: () => { queries++; return typeof mouse === "function" ? mouse(context) : mouse; },
        nookIPC: {
            send: (...args) => ipc.push(args),
            sendSync: (...args) => { requests.push(args); return options.openFails ? null : { x: 10, y: 10, width: 248, height: 460 }; },
            on: (name, fn) => handlers.set(name, fn),
        },
        stopLong: async () => { finishes++; context.nookFinishing = true; },
        requestAnimationFrame: callback => callback(0),
        nookWaitForCapturePaint: async () => {},
        setTimeout: callback => { callback(); return 1; },
        getNowScreen: () => ({ capture: async () => {
            captures++;
            if (options.capture) return options.capture(context);
            return { toImageData: () => ({ width: 800, height: 600 }) };
        } }),
    });
    vm.runInContext(compile(["nookDispose", "nookCaptureFrame", "nookCaptureKeydown", "nookRequestFinish"].map(name => sessionFunctions.get(name)).join("\n")), context);
    vm.runInContext(compile(panelSource), context);
    vm.runInContext(keydownWiring.getText(sessionAst), context);
    const read = expression => vm.runInContext(expression, context);
    return {
        context, body, preview, tip, ipc, sent, cleanup, canvases, requests, handlers, read,
        start() { context.nookStartPanel(context.finalRect); },
        setMouse(value) { mouse = value; },
        get lastState() { return ipc.filter(([name]) => name === "nook-panel-update").at(-1)?.[1]; },
        get selection() { return JSON.parse(JSON.stringify(requests.find(([name]) => name === "nook-panel-open")[1].selection)); },
        get queries() { return queries; }, get finishes() { return finishes; }, get captures() { return captures; },
        keydown(key, values = {}) {
            const event = { key, target: body, defaultPrevented: false, immediateStopped: false,
                preventDefault() { this.defaultPrevented = true; }, stopImmediatePropagation() { this.immediateStopped = true; }, ...values };
            assert.equal(keys.get("keydown").capture, true);
            keys.get("keydown").fn(event);
            return event;
        },
    };
}
function insideViewport(bounds, width, height) {
    assert.ok(bounds.x >= 12 && bounds.y >= 12);
    assert.ok(bounds.x + bounds.width <= width - 12);
    assert.ok(bounds.y + bounds.height <= height - 12);
}

test("sampling renderer never adds interactive chrome or polls pointer state", () => {
    const h = harness();
    assert.equal(h.body.children.length, 0);
    assert.equal(h.preview.hidden, true);
    assert.equal(h.preview.style.display, "none");
    assert.equal(h.captures, 0);
    h.start();
    for (let i = 0; i < 100; i++) h.context.nookUpdatePointer();
    assert.equal(h.queries, 0, "no hover round trip is required for native panel hit testing");
    assert.deepEqual(h.sent, []);
    assert.equal(h.requests.length, 1);
    assert.equal(h.requests[0][0], "nook-panel-open");
    assert.equal(h.lastState.finishEnabled, false);
    assert.match(h.lastState.status, /第一屏/);
    h.start();
    assert.equal(h.requests.length, 1, "duplicate start cannot create two control windows");
});

test("renderer sends selection geometry only; native host owns separate preview and toolbar layout", () => {
    const h = harness();
    h.start();
    assert.deepEqual(h.selection, { x: 100, y: 100, width: 800, height: 600 });
    assert.deepEqual(Object.keys(h.requests[0][1]), ["selection"]);
});

test("panel text respects host limits without losing resume, image metadata or short messages", () => {
    const h = harness(); h.start();
    h.context.longX.img = { width: 800, height: 2400 };
    for (const [statusLength, titleLength] of [[239, 499], [240, 500], [241, 501], [1600, 1600]]) {
        const status = "错".repeat(statusLength), title = "径".repeat(titleLength);
        h.context.nookSendPanel(status, true, "data:image/png;base64,AA==", true, title, true);
        const state = h.lastState;
        assert.ok(state.status.length <= 240);
        assert.ok(state.title.length <= 500);
        assert.equal(state.status, statusLength <= 240 ? status : `${"错".repeat(239)}…`);
        assert.equal(state.title, titleLength <= 500 ? title : `${"径".repeat(499)}…`);
        assert.equal(state.finishEnabled, true);
        assert.equal(state.resume, true);
        assert.equal(state.scrollToTop, true);
        assert.equal(state.preview, "data:image/png;base64,AA==");
        assert.equal(state.imageWidth, 800);
        assert.equal(state.imageHeight, 2400);
    }
    h.context.nookSendPanel(`${"错".repeat(238)}📁更多`, true, undefined, false, `${"径".repeat(498)}📁更多`, true);
    assert.equal(h.lastState.status, `${"错".repeat(238)}…`, "do not split a surrogate pair at the truncation edge");
    assert.equal(h.lastState.title, `${"径".repeat(498)}…`);
});

test("full-width selection remains unchanged rather than being replaced by preview bounds", () => {
    const h = harness();
    assert.deepEqual(JSON.parse(JSON.stringify(h.context.nookPanelSelection([0, 0, 1920, 1080], 1))), { x: 0, y: 0, width: 1920, height: 1080 });
});

test("secondary monitor's negative origin is not added twice to local DIP layout", () => {
    const h = harness({ width: 1280, height: 720, ratio: 1.5, rect: [180, 45, 900, 900] });
    h.start();
    assert.deepEqual(h.selection, { x: 120, y: 30, width: 600, height: 600 });
    h.setMouse({ ab: { x: -1600, y: -900 }, po: { x: 320, y: 180 }, overPanel: false });
    assert.equal(h.context.nookCanCaptureInput(), true);
    for (const x of [119.99, 720]) {
        h.setMouse({ po: { x, y: 180 }, overPanel: false });
        assert.equal(h.context.nookCanCaptureInput(), false);
    }
});

test("native panel startup failure is explicit and never leaves a false usable state", () => {
    const h = harness({ openFails: true });
    assert.throws(() => h.start(), /控制面板/);
    assert.equal(h.read("nookPanelOpen"), false);
    assert.equal(h.ipc.length, 0);
    assert.equal(h.captures, 0);
});

test("bounded thumbnail includes the whole stitched image in either scrolling direction", () => {
    const h = harness(); h.start();
    const source = { width: 800, height: 30000 };
    h.context.longX.img = source;
    h.context.nookRefreshPanel();
    const tail = h.canvases.at(-1);
    assert.ok(tail.width <= 480);
    assert.ok(tail.height <= 2048);
    assert.equal(tail.draws[0][1], 0);
    assert.equal(tail.draws[0][2], 0, "never crop the accumulated top to preview just its tail");
    assert.equal(tail.draws[0][3], 800);
    assert.equal(tail.draws[0][4], 30000);
    assert.equal(h.lastState.preview, "data:image/png;base64,AA==");
    assert.equal(h.lastState.status, "800 × 30000 px");
    assert.equal(h.lastState.finishEnabled, true);
    assert.equal(h.lastState.imageWidth, 800);
    assert.equal(h.lastState.imageHeight, 30000);
    h.context.nookRefreshPanel(true);
    assert.equal(h.canvases.at(-1).draws[0][2], 0);
    assert.equal(h.lastState.scrollToTop, true);
    assert.deepEqual(source, { width: 800, height: 30000 });
});

test("native finish signal is deduplicated by capture state and retries preserve match warnings", () => {
    const h = harness(); h.start(); h.context.longX.img = { width: 800, height: 1800 };
    h.context.nookRefreshPanel();
    h.handlers.get("nook-panel-finish")(); h.handlers.get("nook-panel-finish")();
    assert.equal(h.finishes, 1);
    h.context.nookFinishingPanel();
    assert.equal(h.lastState.finishEnabled, false);
    h.context.nookFinishing = false;
    h.context.nookResumePanel();
    assert.match(h.lastState.status, /页面仍在变化/);
    assert.equal(h.lastState.finishEnabled, true);
    h.context.nookMatchUnresolved = true;
    h.context.nookResumePanel();
    assert.match(h.lastState.status, /向回.*滚动/);
    assert.equal(h.context.longX.img.height, 1800);
});

test("native panel hit exclusion uses host's current bounds, not its initial rectangle", () => {
    const h = harness(); h.start();
    h.setMouse({ po: { x: 400, y: 300 }, overPanel: true });
    assert.equal(h.context.nookCanCaptureInput(), false, "panel may have been natively dragged inside crop");
    h.setMouse({ po: { x: 400, y: 300 }, overPanel: false });
    assert.equal(h.context.nookCanCaptureInput(), true);
    for (const point of [{ x: -10, y: 300 }, { x: 1290, y: 300 }, { x: 400, y: 99 }, { x: 400, y: 700 }, { x: 900, y: 300 }]) {
        h.setMouse({ po: point, overPanel: false });
        assert.equal(h.context.nookCanCaptureInput(), false);
    }
    h.setMouse(null); assert.equal(h.context.nookCanCaptureInput(), false);
    h.setMouse(context => { context.nookDisposed = true; return { po: { x: 400, y: 300 } }; });
    assert.equal(h.context.nookCanCaptureInput(), false);
    const queries = h.queries;
    assert.equal(h.context.nookCanCaptureInput(), false);
    assert.equal(h.queries, queries, "terminal session cannot query input again");
});

test("repeated sampling never toggles the full-screen dimmer or waits for its paint", async () => {
    const h = harness({ rect: [0, 0, 1280, 800] }); h.start();
    const writes = [];
    h.tip.style = new Proxy({ opacity: ".8" }, { set(target, name, value) {
        writes.push([name, value]); target[name] = value; return true;
    } });
    h.context.nookWaitForCapturePaint = () => { throw new Error("per-frame paint delay"); };
    h.context.setTimeout = () => { throw new Error("per-frame timer delay"); };
    const before = h.ipc.length;
    for (let i = 0; i < 20; i++) await h.context.nookCaptureFrame();
    assert.deepEqual(writes, [], "checking only final opacity misses hide/show flicker inside each frame");
    assert.equal(h.tip.style.opacity, ".8");
    assert.equal(h.captures, 20);
    assert.equal(h.ipc.length, before, "sampling sends no panel hide or mouse passthrough messages");
    assert.equal(h.read("nookPanelOpen"), true);
});

test("sampling failure leaves the stable crop and native controls untouched", async () => {
    const h = harness({ capture: () => { throw new Error("synthetic sample failure"); } }); h.start();
    h.tip.style.opacity = ".9";
    await assert.rejects(h.context.nookCaptureFrame(), /synthetic sample failure/);
    assert.equal(h.tip.style.opacity, ".9");
    assert.equal(h.read("nookPanelOpen"), true);
    assert.equal(h.ipc.some(([name]) => name === "nook-panel-close"), false);
});

test("cancelling during native capture discards late pixels and closes controls exactly once", async () => {
    const callbacks = [];
    const h = harness({ capture: () => new Promise(resolve => callbacks.push(resolve)) }); h.start();
    const pending = h.context.nookCaptureFrame();
    h.context.nookCancelCapture(); h.context.nookCancelCapture();
    callbacks.shift()({ toImageData() { throw new Error("cancelled pixels must not be converted"); } });
    assert.equal(await pending, undefined);
    assert.equal(h.captures, 1);
    assert.equal(await h.context.nookCaptureFrame(), undefined);
    assert.equal(h.captures, 1, "a cancelled session cannot start another native sample");
    assert.equal(h.ipc.filter(([name]) => name === "nook-cancel").length, 1);
    assert.equal(h.ipc.filter(([name]) => name === "nook-panel-close").length, 1);
    assert.equal(h.cleanup.filter(item => item === "hook").length, 1);
    const count = h.ipc.length;
    h.context.nookRefreshPanel(); h.handlers.get("nook-panel-finish")(); h.start();
    assert.equal(h.ipc.length, count);
    assert.equal(h.finishes, 0);
    assert.equal(h.read("nookPanelOpen"), false);
});

test("capture's hidden editor cannot consume drawing shortcuts; one Escape cancels", () => {
    for (const state of [{ running: true, finishing: false }, { running: false, finishing: true }]) {
        const h = harness(); h.start();
        h.context.longRunning = state.running; h.context.nookFinishing = state.finishing;
        for (const key of ["s", "z", "a", "Delete", "ArrowLeft", "Enter", " "]) {
            const event = h.keydown(key);
            assert.equal(event.immediateStopped, true); assert.equal(event.defaultPrevented, true);
        }
        const escape = h.keydown("Escape"); h.keydown("Escape", { repeat: true });
        assert.equal(escape.defaultPrevented, true);
        assert.equal(h.ipc.filter(([name]) => name === "nook-cancel").length, 1);
        assert.equal(h.read("nookPanelOpen"), false);
    }
});

test("completed result leaves editing shortcuts and navigation untouched", () => {
    const h = harness(); h.context.longRunning = false; h.context.nookFinishing = false;
    for (const key of ["s", "z", "a", "Delete", "ArrowLeft", "Enter", " ", "Tab"]) {
        const event = h.keydown(key);
        assert.equal(event.immediateStopped, false); assert.equal(event.defaultPrevented, false);
    }
});
