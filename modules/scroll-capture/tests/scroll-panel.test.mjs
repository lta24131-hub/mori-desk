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
const css = fs.readFileSync(path.join(here, "../host/scroll-panel.css"), "utf8");
const javascript = ts.transpileModule(panelSource, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;

class Element {
    constructor(tag = "div") {
        this.tagName = tag;
        this.children = [];
        this.attributes = new Map();
        this.events = new Map();
        this.style = {};
        this.hidden = false;
        this.disabled = false;
        this.scrollHeight = 0;
        this.scrollTop = 0;
    }
    setAttribute(name, value) { this.attributes.set(name, value); }
    append(...items) {
        for (const child of items) {
            if (child.parent) child.parent.children = child.parent.children.filter(item => item !== child);
            this.children.push(child);
            child.parent = this;
        }
    }
    addEventListener(name, callback) {
        if (!this.events.has(name)) this.events.set(name, []);
        this.events.get(name).push(callback);
    }
    fire(name, values = {}) {
        const event = { stopPropagation() { this.stopped = true; }, ...values };
        this[`on${name}`]?.(event);
        for (const callback of this.events.get(name) ?? []) callback(event);
        return event;
    }
    click() { if (!this.disabled) this.fire("click"); }
    setPointerCapture(id) { this.capturedPointerId = id; }
}

// Executes the entire production panel, including its initial DOM mutations,
// event wiring and closures. Only DOM/native transport/screen boundaries are
// fake; there is no substitute panel state implementation or actual capture.
function harness(options = {}) {
    const body = new Element("body");
    const windowEvents = new Map();
    const windowCapture = new Map();
    const preview = new Element();
    const finish = new Element("button");
    const tip = new Element();
    const ipc = [];
    const sent = [];
    const cleanup = [];
    let mouse = options.mouse ?? { po: { x: 400, y: 300 } };
    let queries = 0;
    let finishes = 0;
    let captures = 0;
    const context = vm.createContext({
        console,
        document: { body, createElement: tag => new Element(tag) },
        window: {
            innerWidth: options.width ?? 1280, innerHeight: options.height ?? 800,
            addEventListener(name, callback, capture) {
                if (!windowEvents.has(name)) windowEvents.set(name, []);
                windowEvents.get(name).push(callback);
                windowCapture.set(name, capture);
            },
        },
        longPreview: { el: preview }, finishLongB: finish, longTip: { el: { el: tip } },
        nookDisposed: false, nookFinishing: false, longRunning: true,
        longX: {}, finalRect: options.rect ?? [100, 100, 800, 600], ratio: options.ratio ?? 1,
        longClipTime: "capture-interval", longMouse: "mouse-interval",
        uIOhook: { removeAllListeners: () => cleanup.push("listeners"), stop: () => cleanup.push("hook") },
        clearInterval: value => cleanup.push(value),
        nookClearScheduledFrame: () => cleanup.push("scheduled-frame"),
        renderSend: (...args) => sent.push(JSON.parse(JSON.stringify(args))),
        renderSendSync: () => { queries++; return typeof mouse === "function" ? mouse(context) : mouse; },
        nookIPC: { send: (...args) => ipc.push(args) },
        stopLong: async () => { finishes++; },
        requestAnimationFrame: callback => callback(0),
        setTimeout: callback => { callback(); return 1; },
        getNowScreen: () => ({ capture: async () => {
            captures++;
            if (options.capture) return options.capture(context);
            return { toImageData: () => ({ width: 800, height: 600 }) };
        } }),
    });
    const functions = ["nookDispose", "nookCaptureFrame", "nookCaptureKeydown"].map(name => {
        assert.ok(sessionFunctions.has(name), `production session function missing: ${name}`);
        return sessionFunctions.get(name);
    }).join("\n");
    vm.runInContext(ts.transpileModule(functions, {
        compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
    }).outputText, context);
    vm.runInContext(javascript, context);
    vm.runInContext(keydownWiring.getText(sessionAst), context);
    const read = expression => vm.runInContext(expression, context);
    return {
        context, body, preview, finish, tip, ipc, sent, cleanup, windowEvents, windowCapture,
        panel: read("nookPanel"), header: read("nookPanelHeader"),
        cancel: read("nookPanelCancel"), status: read("nookPanelStatus"), hint: read("nookPanelHint"),
        read,
        setMouse(value) { mouse = value; },
        start() { context.nookStartPanel(context.finalRect); },
        move(x, y) { for (const callback of windowEvents.get("mousemove") ?? []) callback({ clientX: x, clientY: y }); },
        keydown(key, values = {}) {
            const event = {
                key, target: body, defaultPrevented: false, immediateStopped: false,
                preventDefault() { this.defaultPrevented = true; },
                stopImmediatePropagation() { this.immediateStopped = true; },
                ...values,
            };
            for (const callback of windowEvents.get("keydown") ?? []) {
                callback(event);
                if (event.immediateStopped) break;
            }
            return event;
        },
        get bounds() { return JSON.parse(JSON.stringify(read("nookPanelBounds"))); },
        get queries() { return queries; }, get finishes() { return finishes; }, get captures() { return captures; },
    };
}
function rectRight(rect, scale) { return (rect[0] + rect[2]) / scale; }
function insideViewport(bounds, width, height) {
    assert.ok(bounds.x >= 12 && bounds.y >= 12);
    assert.ok(bounds.x + bounds.width <= width - 12);
    assert.ok(bounds.y + bounds.height <= height - 12);
}

test("real top-level panel assembles one persistent accessible shell without starting a capture", () => {
    const h = harness();
    assert.equal(h.body.children.length, 1);
    assert.equal(h.body.children[0], h.panel);
    assert.equal(h.panel.hidden, true);
    assert.equal(h.panel.attributes.get("aria-label"), "滚动截屏实时预览");
    assert.equal(h.status.attributes.get("role"), "status");
    assert.equal(h.status.attributes.get("aria-live"), "polite");
    assert.equal(h.preview.parent, h.panel);
    assert.equal(h.finish.parent.tagName, "footer");
    assert.equal(h.cancel.parent, h.finish.parent);
    assert.equal(h.finish.textContent, "完成");
    assert.equal(h.captures, 0);
    assert.equal(h.queries, 0);
    assert.deepEqual(h.ipc, []);
});

test("layout chooses available right or left space outside the physical crop", () => {
    const h = harness();
    const rightRect = [100, 60, 700, 500];
    const right = h.context.nookLayoutPanel(rightRect, 1, 1920, 1080);
    assert.equal(right.x, 836);
    assert.equal(right.width, 248);
    assert.ok(right.x > rectRight(rightRect, 1));
    insideViewport(right, 1920, 1080);
    const leftRect = [1100, 80, 700, 800];
    const left = h.context.nookLayoutPanel(leftRect, 1, 1920, 1080);
    assert.equal(left.x, 816);
    assert.ok(left.x + left.width < leftRect[0]);
    insideViewport(left, 1920, 1080);
});

test("full-width crops prefer below/above and fullscreen or small viewports clamp the panel", () => {
    const h = harness();
    const below = h.context.nookLayoutPanel([0, 0, 1920, 500], 1, 1920, 1400);
    assert.equal(below.y, 536);
    const above = h.context.nookLayoutPanel([0, 850, 1920, 300], 1, 1920, 1400);
    assert.equal(above.y + above.height, 814);
    for (const [width, height] of [[1920, 1080], [1280, 720], [320, 240], [48, 48]]) {
        const bounds = h.context.nookLayoutPanel([0, 0, width, height], 1, width, height);
        insideViewport(bounds, width, height);
        assert.ok(bounds.width <= 248 && bounds.height <= 460);
    }
});

test("negative-origin secondary monitor crop is laid out in local 1.5-DPI CSS coordinates once", () => {
    const h = harness({ width: 1280, height: 720, ratio: 1.5, rect: [180, 45, 900, 900] });
    h.start();
    const bounds = h.bounds;
    assert.equal(bounds.x, 756);
    assert.equal(bounds.y, 30);
    assert.equal(h.panel.style.left, "756px");
    assert.equal(h.panel.style.top, "30px");
    assert.ok(bounds.x > 720);
    insideViewport(bounds, 1280, 720);
    // The host already removed monitor origin (-1920,-1080). Input supplied
    // to the panel must not add that absolute origin or multiply DPI again.
    h.setMouse({ ab: { x: -1600, y: -900 }, po: { x: 320, y: 180 } });
    assert.equal(h.context.nookCanCaptureInput(), true);
    h.setMouse({ po: { x: 119.99, y: 180 } });
    assert.equal(h.context.nookCanCaptureInput(), false);
    h.setMouse({ po: { x: 720, y: 180 } });
    assert.equal(h.context.nookCanCaptureInput(), false);
});

test("finish remains disabled until a stitched first frame is actually available", () => {
    const h = harness();
    h.start();
    assert.equal(h.panel.hidden, false);
    assert.equal(h.finish.disabled, true);
    assert.match(h.status.textContent, /第一屏/);
    h.finish.click();
    h.context.nookRefreshPanel();
    assert.equal(h.finishes, 0);
    assert.equal(h.finish.disabled, true);
    h.context.longX.img = { width: 800, height: 600 };
    h.context.nookRefreshPanel();
    assert.equal(h.finish.disabled, false);
    assert.match(h.status.textContent, /800 × 600/);
    h.finish.click();
    assert.equal(h.finishes, 1);
});

test("long preview stays width-fit/scrollable, follows the tail and keeps buttons outside replaced image content", () => {
    const h = harness();
    h.start();
    const footer = h.finish.parent;
    h.context.longX.img = { width: 800, height: 12000 };
    h.preview.scrollHeight = 3456;
    h.preview.append(new Element("canvas"));
    h.context.nookRefreshPanel();
    assert.equal(h.preview.scrollTop, 3456);
    assert.match(h.status.textContent, /800 × 12000/);
    h.context.nookRefreshPanel(true);
    assert.equal(h.preview.scrollTop, 0);
    h.preview.children = [];
    h.preview.append(new Element("canvas"));
    assert.equal(h.finish.parent, footer);
    assert.equal(h.cancel.parent, footer);
    assert.ok(h.panel.children.includes(footer));
    assert.match(css, /#nook_scroll_panel #nook_scroll_preview\s*\{[^}]*overflow:\s*auto/s);
    assert.match(css, /#nook_scroll_preview canvas\s*\{[^}]*width:\s*100%/s);
    assert.match(css, /#nook_scroll_preview canvas\s*\{[^}]*height:\s*auto/s);
    assert.match(css, /#nook_scroll_preview canvas\s*\{[^}]*max-height:\s*none/s);
});

test("panel is an opaque input island with half-open edge hit tests, not a source-page scroll target", () => {
    const h = harness({ rect: [0, 0, 1280, 800] });
    h.start();
    const b = h.bounds;
    assert.equal(h.context.nookIsPanelPoint(b.x, b.y), true);
    assert.equal(h.context.nookIsPanelPoint(b.x + b.width - 0.01, b.y + b.height - 0.01), true);
    assert.equal(h.context.nookIsPanelPoint(b.x + b.width, b.y), false);
    assert.equal(h.context.nookIsPanelPoint(b.x, b.y + b.height), false);
    h.setMouse({ po: { x: b.x + 15, y: b.y + 50 } });
    assert.equal(h.context.nookCanCaptureInput({ type: "wheel" }), false);
    for (const name of ["pointerdown", "pointerup", "mousedown", "mouseup", "click", "dblclick", "wheel"])
        assert.equal(h.panel.fire(name).stopped, true, `${name} must not reach selection/pen handlers`);
    assert.match(css, /#nook_scroll_panel\s*\{[^}]*pointer-events:\s*auto/s);
});

test("pointer polling and forwarded mouse movement switch click-through only when the island changes", () => {
    const h = harness({ rect: [0, 0, 1280, 800] });
    h.start();
    assert.deepEqual(h.sent.at(-1), ["windowIgnoreMouse", [true]]);
    const b = h.bounds;
    const before = h.sent.length;
    h.context.nookUpdatePointer();
    assert.equal(h.sent.length, before, "unchanged pointer state must not flood native IPC");
    h.setMouse({ po: { x: b.x + 10, y: b.y + 10 } });
    h.context.nookUpdatePointer();
    assert.deepEqual(h.sent.at(-1), ["windowIgnoreMouse", [false]]);
    h.move(200, 200);
    assert.deepEqual(h.sent.at(-1), ["windowIgnoreMouse", [true]]);
    h.move(b.x + 20, b.y + 20);
    assert.deepEqual(h.sent.at(-1), ["windowIgnoreMouse", [false]]);
    h.setMouse(null);
    assert.doesNotThrow(() => h.context.nookUpdatePointer());
    const queries = h.queries;
    h.context.nookDisposed = true;
    h.context.nookUpdatePointer();
    h.move(200, 200);
    assert.equal(h.queries, queries);
});

test("opacity-only sampling preserves panel input geometry without setting DOM visibility hidden", async () => {
    let active;
    const h = harness({ rect: [0, 0, 1280, 800], capture: context => {
        assert.equal(active.panel.style.opacity, "0");
        assert.equal(active.tip.style.opacity, "0");
        assert.equal(active.panel.style.visibility, "");
        assert.equal(active.tip.style.visibility, "visible");
        assert.equal(active.panel.hidden, false, "sampling must not collapse or disable the panel");
        context.nookUpdatePointer();
        assert.deepEqual(active.sent.at(-1), ["windowIgnoreMouse", [false]]);
        assert.equal(context.nookCanCaptureInput(), false);
        return { toImageData: () => ({ width: 1280, height: 800 }) };
    } });
    active = h;
    h.start();
    const b = h.bounds;
    h.setMouse({ po: { x: b.x + 20, y: b.y + b.height - 40 } });
    h.panel.style.visibility = "";
    h.tip.style.visibility = "visible";
    h.panel.style.opacity = ".85";
    h.tip.style.opacity = "";
    const image = await h.context.nookCaptureFrame();
    assert.equal(image.width, 1280);
    assert.equal(h.captures, 1);
    assert.equal(h.panel.style.visibility, "");
    assert.equal(h.tip.style.visibility, "visible");
    assert.equal(h.panel.style.opacity, ".85");
    assert.equal(h.tip.style.opacity, "");
});

test("default outside placement keeps panel and shadow stable while a near-fullscreen crop excludes overlap", () => {
    const outside = harness({ width: 1920, height: 1080, rect: [100, 100, 800, 600] });
    outside.start();
    assert.deepEqual(Array.from(outside.context.nookCaptureOverlays()), [outside.tip]);
    outside.read("nookPanelBounds.x -= 24; nookApplyPanelBounds();");
    assert.deepEqual(Array.from(outside.context.nookCaptureOverlays()), [outside.tip, outside.panel], "dragging the panel close enough to let its shadow enter the crop must exclude it");
    const overlap = harness({ rect: [0, 0, 1280, 800] });
    overlap.start();
    assert.deepEqual(Array.from(overlap.context.nookCaptureOverlays()), [overlap.tip, overlap.panel]);
    overlap.context.nookHidePanel();
    assert.deepEqual(Array.from(overlap.context.nookCaptureOverlays()), [overlap.tip]);
    assert.equal(overlap.context.nookIsPanelPoint(1100, 200), false);
});

test("a sampling failure restores previous opacity without changing visibility or panel input geometry", async () => {
    const h = harness({ rect: [0, 0, 1280, 800], capture: () => { throw new Error("synthetic sample failure"); } });
    h.start();
    h.panel.style.visibility = "visible";
    h.tip.style.visibility = "";
    h.panel.style.opacity = ".7";
    h.tip.style.opacity = ".9";
    await assert.rejects(h.context.nookCaptureFrame(), /synthetic sample failure/);
    assert.equal(h.panel.hidden, false);
    assert.equal(h.panel.style.visibility, "visible");
    assert.equal(h.tip.style.visibility, "");
    assert.equal(h.panel.style.opacity, ".7");
    assert.equal(h.tip.style.opacity, ".9");
    assert.equal(h.context.nookIsPanelPoint(h.bounds.x + 20, h.bounds.y + 20), true);
});

test("cancelling during the transparent paint delay cannot sample or restore a ghost panel afterwards", async () => {
    const callbacks = [];
    const h = harness({ rect: [0, 0, 1280, 800] });
    h.start();
    h.context.requestAnimationFrame = callback => callbacks.push(callback);
    const pending = h.context.nookCaptureFrame();
    assert.equal(h.panel.style.opacity, "0");
    assert.notEqual(h.panel.style.visibility, "hidden");
    h.context.nookCancelCapture();
    callbacks.shift()(0);
    callbacks.shift()(0);
    assert.equal(await pending, undefined);
    assert.equal(h.captures, 0);
    assert.equal(h.panel.hidden, true);
    assert.deepEqual(h.ipc, [["nook-cancel"]]);
    assert.deepEqual(h.sent.at(-1), ["windowIgnoreMouse", [false]]);
});

test("selection filtering rejects another monitor, the panel, and inactive or dragging sessions", () => {
    const h = harness();
    h.start();
    for (const point of [{ x: -10, y: 300 }, { x: 1290, y: 300 }, { x: 400, y: 99 }, { x: 400, y: 700 }, { x: 900, y: 300 }]) {
        h.setMouse({ po: point });
        assert.equal(h.context.nookCanCaptureInput(), false, JSON.stringify(point));
    }
    h.setMouse({ po: { x: 100, y: 100 } });
    assert.equal(h.context.nookCanCaptureInput(), true);
    h.setMouse(null);
    assert.equal(h.context.nookCanCaptureInput(), false);
    h.setMouse(context => { context.nookDisposed = true; return { po: { x: 400, y: 300 } }; });
    assert.equal(h.context.nookCanCaptureInput(), false, "terminal reply race must not enqueue work");
    h.context.nookDisposed = false;
    h.setMouse({ po: { x: 400, y: 300 } });
    for (const state of ["nookDisposed", "nookFinishing", "longRunning"]) {
        h.context[state] = state !== "longRunning";
        const queries = h.queries;
        assert.equal(h.context.nookCanCaptureInput(), false, state);
        assert.equal(h.queries, queries, `${state} must reject before synchronous IPC`);
        h.context[state] = state === "longRunning";
    }
    h.header.fire("pointerdown", { button: 0, clientX: h.bounds.x, clientY: h.bounds.y, pointerId: 9 });
    assert.equal(h.context.nookCanCaptureInput(), false);
});

test("panel header drags clamp to viewport and preserve native input until pointer release or cancellation", () => {
    const h = harness();
    h.start();
    const b = h.bounds;
    h.header.fire("pointerdown", { button: 2, clientX: b.x, clientY: b.y, pointerId: 1 });
    assert.equal(h.read("nookPanelDrag"), undefined);
    h.header.fire("pointerdown", { button: 0, clientX: b.x, clientY: b.y, pointerId: 2 });
    assert.equal(h.header.capturedPointerId, 2);
    assert.deepEqual(h.sent.at(-1), ["windowIgnoreMouse", [false]]);
    h.setMouse({ po: { x: -20, y: -20 } });
    h.context.nookUpdatePointer();
    h.move(200, 200);
    assert.deepEqual(h.sent.at(-1), ["windowIgnoreMouse", [false]], "header drag owns input even away from the original panel rectangle");
    h.header.fire("pointermove", { clientX: 9000, clientY: 9000 });
    insideViewport(h.bounds, 1280, 800);
    assert.equal(h.bounds.x + h.bounds.width, 1268);
    h.header.fire("pointermove", { clientX: -9000, clientY: -9000 });
    assert.equal(h.bounds.x, 12);
    assert.equal(h.bounds.y, 12);
    h.header.fire("pointercancel");
    assert.equal(h.read("nookPanelDrag"), undefined);
    h.header.fire("lostpointercapture");
    assert.equal(h.read("nookPanelDrag"), undefined);
    h.context.nookHidePanel();
    assert.equal(h.panel.hidden, true);
    assert.equal(h.read("nookPointerIgnored"), undefined);
});

test("finishing disables duplicate finish but keeps cancel functional and terminal cleanup idempotent", () => {
    const h = harness();
    h.start();
    h.context.longX.img = { width: 800, height: 1800 };
    h.context.nookRefreshPanel();
    h.context.nookFinishing = true;
    h.context.nookFinishingPanel();
    assert.equal(h.finish.disabled, true);
    assert.equal(h.cancel.disabled, false);
    assert.match(h.status.textContent, /最后一屏/);
    assert.match(h.hint.textContent, /标注/);
    h.finish.click();
    assert.equal(h.finishes, 0);
    h.context.nookRefreshPanel();
    assert.equal(h.finish.disabled, true);
    h.cancel.click();
    h.cancel.click();
    h.context.nookCancelCapture();
    assert.deepEqual(h.ipc, [["nook-cancel"]]);
    assert.equal(h.context.nookDisposed, true);
    assert.equal(h.context.longRunning, false);
    assert.equal(h.context.uIOhook, null);
    assert.equal(h.panel.hidden, true);
    assert.equal(h.cleanup.filter(item => item === "listeners").length, 1);
    assert.equal(h.cleanup.filter(item => item === "hook").length, 1);
    assert.equal(h.cleanup.filter(item => item === "scheduled-frame").length, 1);
    assert.ok(h.cleanup.includes("capture-interval") && h.cleanup.includes("mouse-interval"));
    assert.deepEqual(h.sent.at(-1), ["windowIgnoreMouse", [false]]);
    const sent = h.sent.length;
    const queries = h.queries;
    h.context.nookUpdatePointer();
    h.context.nookRefreshPanel();
    h.move(500, 500);
    assert.equal(h.sent.length, sent);
    assert.equal(h.queries, queries);
});

test("live and finishing capture intercept editor shortcuts before they can mutate the fixed crop", () => {
    for (const state of [{ running: true, finishing: false }, { running: false, finishing: true }]) {
        const h = harness();
        h.start();
        h.context.longRunning = state.running;
        h.context.nookFinishing = state.finishing;
        assert.equal(h.windowCapture.get("keydown"), true, "keyboard isolation must run during the DOM capture phase");
        for (const [key, modifiers] of [
            ["s", {}], ["z", { ctrlKey: true }], ["a", { ctrlKey: true }],
            ["c", { metaKey: true }], ["Delete", {}], ["ArrowLeft", {}],
            ["Enter", {}], [" ", {}],
        ]) {
            const event = h.keydown(key, modifiers);
            assert.equal(event.immediateStopped, true, `${key}: upstream editor must not see this key`);
            assert.equal(event.defaultPrevented, true, `${key}: native edit action must be blocked`);
        }
        assert.deepEqual(h.ipc, []);
        assert.equal(h.context.nookDisposed, false);
    }
});

test("capture preserves native Tab navigation and Finish/Cancel Enter or Space activation", () => {
    for (const state of [{ running: true, finishing: false }, { running: false, finishing: true }]) {
        const h = harness();
        h.start();
        h.context.longRunning = state.running;
        h.context.nookFinishing = state.finishing;
        for (const shiftKey of [false, true]) {
            const tab = h.keydown("Tab", { shiftKey });
            assert.equal(tab.immediateStopped, true);
            assert.equal(tab.defaultPrevented, false, "Tab's native focus navigation must remain available");
        }
        for (const target of [h.finish, h.cancel]) {
            for (const key of ["Enter", " "]) {
                const event = h.keydown(key, { target });
                assert.equal(event.immediateStopped, true);
                assert.equal(event.defaultPrevented, false, "the actual native button activation must remain available");
            }
        }
        // VM dispatch verifies default-action policy, not native focus or clicks.
        assert.equal(h.finishes, 0);
        assert.deepEqual(h.ipc, []);
    }
});

test("one Escape cancels live or finishing capture and repeat delivery cannot duplicate cancellation", () => {
    for (const state of [{ running: true, finishing: false }, { running: false, finishing: true }]) {
        const h = harness();
        h.start();
        h.context.longRunning = state.running;
        h.context.nookFinishing = state.finishing;
        const first = h.keydown("Escape", { target: h.finish });
        assert.equal(first.defaultPrevented, true);
        assert.equal(first.immediateStopped, true);
        assert.equal(h.context.nookDisposed, true);
        assert.equal(h.panel.hidden, true);
        assert.deepEqual(h.ipc, [["nook-cancel"]]);
        h.keydown("Escape", { repeat: true });
        assert.deepEqual(h.ipc, [["nook-cancel"]]);
        assert.equal(h.cleanup.filter(item => item === "scheduled-frame").length, 1);
        assert.equal(h.cleanup.filter(item => item === "hook").length, 1);
    }
});

test("completed capture leaves normal editing shortcuts and navigation untouched", () => {
    const h = harness();
    h.context.longRunning = false;
    h.context.nookFinishing = false;
    for (const [key, modifiers] of [
        ["s", {}], ["z", { ctrlKey: true }], ["a", { ctrlKey: true }],
        ["Delete", {}], ["ArrowLeft", {}], ["Enter", {}], [" ", {}], ["Tab", {}],
    ]) {
        const event = h.keydown(key, modifiers);
        assert.equal(event.immediateStopped, false, `${key}: completed image's editor must receive the key`);
        assert.equal(event.defaultPrevented, false, `${key}: normal editor defaults must remain available`);
    }
    assert.deepEqual(h.ipc, []);
    assert.equal(h.context.nookDisposed, false);
});
