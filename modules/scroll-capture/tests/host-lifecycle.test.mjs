import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import fs from "node:fs";
import { createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import vm from "node:vm";
import * as protocol from "../host/protocol.mjs";
import * as panelLayout from "../host/panel-layout.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
function compiler() {
    try { return require("typescript"); }
    catch {
        const root = process.env.NOOK_ESEARCH_SOURCE || path.resolve(here, "../../../outputs/esearch-15.6.0-source");
        return require(path.join(root, "node_modules/typescript/lib/typescript.js"));
    }
}
const ts = compiler();
const source = fs.readFileSync(path.join(here, "../host/main.ts"), "utf8");
const javascript = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, esModuleInterop: true },
}).outputText;
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aavsAAAAASUVORK5CYII=", "base64");

// Execute the production host, not a parallel model of its state machine.
// Electron is replaced at its transport/window boundary, so no native window,
// screen capture, input hook, clipboard access, or real library is reachable.
async function harness(t, options = {}) {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nook-scroll-host-test-"));
    t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const requestPath = path.join(directory, "request.json");
    const request = {
        schemaVersion: 1, sessionId: "cafe0123456789abcdef0123456789ab", outputDirectory: directory,
        region: { x: -1800, y: -1050, width: 800, height: 1000 },
        monitor: { x: -1920, y: -1080, width: 1920, height: 1080, scaleFactor: 1.5 },
    };
    if (options.parentProcessId || options.warm) request.parentProcessId = options.parentProcessId || 4242;
    if (options.region) request.region = options.region;
    if (options.transactionalOutputs) request.transactionalOutputs = true;
    if (Object.hasOwn(options, "deferredStart")) request.deferredStart = options.deferredStart;
    if (options.features) request.features = options.features;
    const warmPath = path.join(directory, "warm.json");
    if (options.warm) {
        request.deferredStart = true;
        fs.writeFileSync(warmPath, JSON.stringify({ schemaVersion: 1, sessionId: request.sessionId, outputDirectory: directory, parentProcessId: request.parentProcessId }));
    } else fs.writeFileSync(requestPath, JSON.stringify(request));
    const app = new EventEmitter();
    const exits = [];
    app.setPath = () => {};
    app.setName = () => {};
    app.exit = (code) => exits.push(code);
    app.whenReady = () => Promise.resolve();
    app.isReady = () => true;
    const ipcMain = new EventEmitter();
    const shortcuts = new Map();
    const activeShortcuts = new Set();
    const shortcutCalls = [];
    const globalShortcut = {
        register(key, callback) { shortcuts.set(key, callback); if (!options.escapeReserved) activeShortcuts.add(key); return !options.escapeReserved; },
        unregisterAll() { shortcutCalls.push("unregisterAll"); activeShortcuts.clear(); },
    };
    const windows = [];
    let releaseRendererLoad;
    const rendererLoad = options.deferRendererLoad ? new Promise(resolve => { releaseRendererLoad = resolve; }) : Promise.resolve();
    class BrowserWindow extends EventEmitter {
        constructor(configuration) {
            super(); this.configuration = configuration; this.destroyed = false;
            this.mouseModes = []; this.sent = []; this.boundsChanges = [];
            this.webContents = new EventEmitter();
            this.webContents.id = 41 + windows.length;
            this.rendererIPC = new EventEmitter();
            this.webContents.send = (...args) => {
                this.sent.push(args);
                this.rendererIPC.emit(args[0], {}, ...args.slice(1));
            };
            this.webContents.setWindowOpenHandler = (callback) => { this.openHandler = callback; };
            windows.push(this);
        }
        isDestroyed() { return this.destroyed; }
        setIgnoreMouseEvents(...args) { this.mouseModes.push(args); }
        setMenu() {}
        setContentProtection(value) { this.contentProtection = value; }
        isContentProtected() { return !(this.webContents.id === 41 ? options.samplerProtectionUnavailable : options.protectionUnavailable) && this.contentProtection; }
        setAlwaysOnTop(value) { this.alwaysOnTop = value; }
        setSkipTaskbar(value) { this.skipTaskbar = value; }
        setFocusable(value) { this.focusable = value; }
        setMovable(value) { this.movable = value; }
        setResizable(value) { this.resizable = value; }
        setMinimumSize(...values) { this.minimum = values; }
        setBounds(value) { this.resultBounds = value; this.boundsChanges.push(value); }
        setTitle(value) { this.title = value; }
        show() { this.shown = true; this.showCount = (this.showCount ?? 0) + 1; }
        showInactive() { this.shown = true; this.inactiveShow = true; this.inactiveShowCount = (this.inactiveShowCount ?? 0) + 1; }
        hide() { this.shown = false; this.hideCount = (this.hideCount ?? 0) + 1; }
        isVisible() { return this.shown === true; }
        focus() { this.focused = true; }
        getBounds() { const { x, y, width, height } = this.resultBounds ?? this.configuration; return { x, y, width, height }; }
        loadFile() { this.protectedBeforeRendererLoad = this.isContentProtected(); this.rendererLoaded = true; return rendererLoad; }
        loadURL(url) {
            this.url = url;
            if (options.panelLoadFails) return Promise.reject(new Error("synthetic panel load failure"));
            const html = decodeURIComponent(url.slice(url.indexOf(",") + 1));
            this.html = html;
            const nodes = new Map(["edit", "save", "copy", "cancel", "status", "preview", "image", "toolbar"].map(id => [id, {
                disabled: ["edit", "save", "copy"].includes(id), textContent: "", scrollTop: 0, scrollHeight: 1234,
                style: {},
                click() { if (!this.disabled) this.onclick?.(); },
            }]));
            nodes.get("preview").querySelector = () => nodes.get("image");
            const image = nodes.get("image");
            image.srcWrites = [];
            Object.defineProperty(image, "src", {
                get() { return this.srcWrites.at(-1); },
                set(value) { this.srcWrites.push(value); },
            });
            this.elements = nodes;
            this.documentEvents = new Map();
            this.rendererIPC.send = (channel, ...args) => ipcMain.emit(channel, { sender: { id: this.webContents.id } }, ...args);
            const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
            vm.runInNewContext(script, {
                document: { getElementById: id => nodes.get(id), addEventListener: (name, callback) => this.documentEvents.set(name, callback) },
                require: name => { assert.equal(name, "electron"); return { ipcRenderer: this.rendererIPC }; },
            }, { filename: "production-native-panel-script.js" });
            return Promise.resolve();
        }
        destroy() { this.destroyed = true; }
        static getAllWindows() { return windows.filter((window) => !window.destroyed); }
    }
    const display = { id: 1, scaleFactor: options.changedMonitorScale ? 1 : 1.5, bounds: { x: -1280, y: -720, width: 1280, height: 720 } };
    const calls = { nativeRequire: 0, monitors: 0, screen: 0 };
    const screen = {
        screenToDipPoint: (point) => { calls.screen++; return { x: point.x / 1.5, y: point.y / 1.5 }; },
        getDisplayNearestPoint: () => { calls.screen++; return display; },
        getDisplayMatching: () => ({ ...display, workArea: { x: -1280, y: -720, width: 1280, height: 680 } }),
        getAllDisplays: () => { calls.screen++; return [display]; },
        getCursorScreenPoint: () => { calls.screen++; return { x: -1200, y: -700 }; },
    };
    const permissions = {};
    const defaultSession = {
        setPermissionRequestHandler(callback) { permissions.request = callback; },
        setPermissionCheckHandler(callback) { permissions.check = callback; },
        webRequest: { onBeforeRequest(callback) { permissions.network = callback; } },
    };
    const nativeImage = { createFromBuffer: () => ({ isEmpty: () => false, getSize: () => ({ width: 1, height: 1 }) }) };
    const monitor = {
        id: () => 1, x: () => -1920, y: () => -1080, width: () => 1920, height: () => 1080,
    };
    const timers = [];
    const setTimeoutFake = (callback, milliseconds) => {
        const timer = { callback, milliseconds, cleared: false }; timers.push(timer); return timer;
    };
    const clearTimeoutFake = (timer) => { if (timer) timer.cleared = true; };
    const processFake = new EventEmitter();
    processFake.argv = options.warm ? ["Nook.ScrollCapture.exe", "--nook-warm", warmPath] : ["Nook.ScrollCapture.exe", "--nook-request", requestPath];
    const parentProbes = [];
    let parentGone = options.parentMissing === true;
    processFake.kill = (pid, signal) => {
        parentProbes.push([pid, signal]);
        if (parentGone) throw Object.assign(new Error("missing synthetic parent"), { code: "ESRCH" });
    };
    const electron = { app, BrowserWindow, globalShortcut, ipcMain, nativeImage, screen, session: { defaultSession } };
    const errors = [];
    const context = vm.createContext({
        exports: {}, Buffer,
        __dirname: path.join(directory, "out/main"),
        process: processFake,
        setTimeout: setTimeoutFake, clearTimeout: clearTimeoutFake,
        setInterval: setTimeoutFake, clearInterval: clearTimeoutFake,
        console: { log() {}, error: (...args) => errors.push(args) },
        require(name) {
            if (name === "electron") return electron;
            if (name === "./protocol.mjs") return protocol;
            if (name === "./panel-layout.mjs") return panelLayout;
            if (name === "./nook-panel-icons") return Object.fromEntries(["edit", "save", "cancel", "copy"].map(name => [name, `data:image/svg+xml;base64,${Buffer.from(`<svg>${name}</svg>`).toString("base64")}`]));
            if (name === "./nook-defaults") return { default: { test: { value: 1 } } };
            if (name === "node-screenshots") { calls.nativeRequire++; return { Monitor: { all: () => { calls.monitors++; return options.monitorMissing ? [] : [monitor]; } } }; }
            if (name === "node:fs" || name === "node:path") return require(name);
            throw new Error(`Unexpected runtime dependency in headless host: ${name}`);
        },
    });
    vm.runInContext(javascript, context, { filename: "production-scroll-capture-host.js" });
    for (let i = 0; i < 6; i++) await Promise.resolve();
    const event = { sender: { id: 41 } };
    return {
        directory, request, windows, ipcMain, event, shortcuts, activeShortcuts, shortcutCalls, timers, exits, errors, permissions, parentProbes, screen, app, calls,
        publishRequest(value = request) { fs.writeFileSync(requestPath, typeof value === "string" ? value : JSON.stringify(value)); },
        loseParent() { parentGone = true; },
        async releaseRendererLoad() { releaseRendererLoad?.(); for (let i = 0; i < 6; i++) await Promise.resolve(); },
        writeStart(value = { schemaVersion: 1, sessionId: request.sessionId, start: true }) {
            fs.writeFileSync(path.join(directory, "start.request"), typeof value === "string" ? value : JSON.stringify(value));
        },
        result: () => JSON.parse(fs.readFileSync(path.join(directory, "result.json"), "utf8")),
        emit: (name, ...args) => ipcMain.emit(name, event, ...args),
        async openPanel(bounds = { x: 740, y: 30, width: 248, height: 460 }) {
            this.sendSync("nook-panel-open", bounds);
            for (let i = 0; i < 4; i++) await Promise.resolve();
            return windows[1];
        },
        sendSync(name, ...args) {
            // Electron sends the reply when returnValue is assigned; it is not
            // a mutable field whose final value replaces an earlier reply.
            // Preserve the first assignment to catch premature null replies.
            const writes = [];
            const syncEvent = { sender: { id: 41 } };
            Object.defineProperty(syncEvent, "returnValue", { set(value) { writes.push(value); } });
            ipcMain.emit(name, syncEvent, ...args);
            return { value: writes[0], writes };
        },
    };
}

test("warm loads one tiny hidden renderer without native screens, hotkeys or pixels", async (t) => {
    const h = await harness(t, { warm: true });
    const sampler = h.windows[0];
    assert.equal(sampler.configuration.show, false);
    assert.equal(sampler.configuration.focusable, false);
    assert.equal(sampler.configuration.width, 1);
    assert.equal(sampler.configuration.height, 1);
    assert.equal(h.shortcuts.size, 0);
    assert.deepEqual(h.calls, { nativeRequire: 0, monitors: 0, screen: 0 });
    assert.equal(fs.existsSync(path.join(h.directory, "warm.ready.json")), false, "document load alone cannot prove the adapter ran");
    h.emit("nook-renderer-loaded");
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(h.directory, "warm.ready.json"), "utf8")), { schemaVersion: 1, sessionId: h.request.sessionId, warm: true });
    assert.equal(h.sendSync("nook-request").value, null);
    h.sendSync("ipc", "getMousePos", []);
    assert.equal(h.sendSync("ipc", "getScreens", []).value.length, 0);
    h.emit("ipc", "clip_show", []);
    h.emit("nook-complete", "copy", { get length() { assert.fail("warm renderer may not decode pixels"); } });
    h.emit("nook-initialized");
    assert.equal(h.sendSync("nook-panel-open", {}).value, null);
    assert.equal(sampler.shown, undefined);
    assert.equal(h.windows.length, 1);
    assert.deepEqual(h.calls, { nativeRequire: 0, monitors: 0, screen: 0 });
    assert.equal(fs.existsSync(path.join(h.directory, "prepared.json")), false);
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
});

test("warm accepts a request during renderer loading but only captures after validated handoff", async (t) => {
    const h = await harness(t, { warm: true, deferRendererLoad: true, features: { pin: false, collect: false } });
    const warmPoll = h.timers.find(timer => timer.milliseconds === 50);
    const ttl = h.timers.find(timer => timer.milliseconds > 119_000);
    h.publishRequest();
    warmPoll.callback();
    const watchdog = h.timers.find(timer => timer.milliseconds === 18_000);
    assert.ok(watchdog, "activation starts its fresh watchdog even before the renderer finishes loading");
    assert.equal(ttl.cleared, true);
    ttl.callback();
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false, "late TTL cannot cancel an accepted request");
    assert.equal(h.calls.nativeRequire, 0);
    h.emit("nook-renderer-loaded");
    assert.equal(fs.existsSync(path.join(h.directory, "warm.ready.json")), false, "script handshake alone is not document readiness");
    await h.releaseRendererLoad();
    assert.equal(h.calls.nativeRequire, 1);
    assert.equal(h.calls.monitors, 1);
    assert.ok(h.activeShortcuts.has("Escape"));
    assert.deepEqual(JSON.parse(JSON.stringify(h.windows[0].resultBounds)), { x: -1280, y: -720, width: 1280, height: 720 });
    assert.equal(fs.existsSync(path.join(h.directory, "prepared.json")), true);
    assert.equal(h.sendSync("nook-request").value, null, "even prepared renderer has no permission to sample");
    const starts = () => h.windows[0].sent.filter(item => item[0] === "ipc" && item[1] === "clip_init");
    assert.equal(starts().length, 0);
    h.writeStart();
    h.timers.find(timer => timer.milliseconds === 30).callback();
    assert.equal(starts().length, 1);
    assert.equal(h.sendSync("nook-request").value.features.pin, false);
    warmPoll.callback(); ttl.callback();
    assert.equal(starts().length, 1);
    h.emit("nook-initialized");
    await h.openPanel();
    assert.equal(watchdog.cleared, true);
    const timing = JSON.parse(fs.readFileSync(path.join(h.directory, "startup-timing.json"), "utf8"));
    assert.equal(timing.warm, true);
    for (const phase of ["appReadyMs", "activatedMs", "rendererLoadedMs", "preparedMs", "captureStartMs", "readyMs"]) assert.ok(timing[phase] >= 0, phase);
    assert.equal(JSON.stringify(timing).includes(h.directory), false);
});

test("published warm activation wins expiry and receives a new timeout, not a lifetime extension", async (t) => {
    const h = await harness(t, { warm: true, deferRendererLoad: true });
    h.publishRequest();
    const ttl = h.timers.find(timer => timer.milliseconds > 119_000);
    ttl.callback();
    assert.equal(ttl.cleared, true);
    const watchdog = h.timers.find(timer => timer.milliseconds === 18_000);
    watchdog.callback();
    assert.equal(h.result().status, "error");
    h.emit("nook-renderer-loaded");
    await h.releaseRendererLoad();
    assert.equal(h.calls.nativeRequire, 0);
    assert.equal(h.windows[0].shown, undefined);
});

test("unused warm cancellation, expiry, parent death and renderer failure exit silently without revival", async (t) => {
    for (const reason of ["cancel", "expiry", "parent", "crash", "quit"]) {
        const h = await harness(t, { warm: true, deferRendererLoad: true });
        if (reason === "cancel") {
            fs.writeFileSync(path.join(h.directory, "cancel.request"), "cancel");
            h.timers.find(timer => timer.milliseconds === 250).callback();
        } else if (reason === "expiry") h.timers.find(timer => timer.milliseconds > 119_000).callback();
        else if (reason === "parent") {
            h.loseParent();
            h.timers.find(timer => timer.milliseconds === 250).callback();
        } else if (reason === "crash") h.windows[0].webContents.emit("render-process-gone", {}, { reason: "crashed" });
        else h.app.emit("before-quit", { preventDefault() {} });
        h.publishRequest();
        h.emit("nook-renderer-loaded");
        await h.releaseRendererLoad();
        for (const timer of h.timers.filter(timer => timer.milliseconds === 50)) { assert.equal(timer.cleared, true); timer.callback(); }
        assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false, reason);
        assert.equal(fs.existsSync(path.join(h.directory, "warm.ready.json")), false, reason);
        assert.deepEqual(h.calls, { nativeRequire: 0, monitors: 0, screen: 0 }, reason);
        assert.equal(h.shortcuts.size, 0, reason);
        assert.equal(h.windows[0].shown, undefined, reason);
        h.timers.find(timer => timer.milliseconds === 350).callback();
        assert.deepEqual(h.exits, [0], reason);
    }
    const dead = await harness(t, { warm: true, parentMissing: true });
    assert.equal(dead.windows.length, 0, "a parent already dead at startup prevents renderer allocation");
    assert.equal(fs.existsSync(path.join(dead.directory, "result.json")), false);
});

test("warm request identity or non-deferred mismatch fails before touching native screens", async (t) => {
    for (const replacement of [{ sessionId: "00000123456789abcdef0123456789ab" }, { parentProcessId: 7 }, { deferredStart: false }, { outputDirectory: os.tmpdir() }]) {
        const h = await harness(t, { warm: true });
        h.emit("nook-renderer-loaded");
        h.publishRequest({ ...h.request, ...replacement });
        h.timers.find(timer => timer.milliseconds === 50).callback();
        assert.equal(h.calls.nativeRequire, 0);
        assert.equal(h.shortcuts.size, 0);
        assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
        assert.equal(fs.existsSync(path.join(h.directory, "prepared.json")), false);
    }
});

test("disabled output destinations are rejected before decoding or publishing pixels", async (t) => {
    const h = await harness(t, { transactionalOutputs: true, features: { pin: false, collect: false } });
    for (const action of ["pin", "collect"]) {
        h.emit("nook-complete", action, { get length() { assert.fail("disabled output attempted to convert image bytes"); } });
        assert.equal(fs.existsSync(path.join(h.directory, "export-1.json")), false);
        assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
        assert.match(h.windows[0].sent.filter(item => item[0] === "nook-export-reply").at(-1)[2], /关闭/);
    }
    h.emit("nook-complete", "copy", png);
    assert.equal(fs.existsSync(path.join(h.directory, "export-1.json")), true, "essential copy remains available after rejected optional outputs");
});

test("deferred startup prepares the hidden helper before accepting one start signal", async (t) => {
    const h = await harness(t, { deferredStart: true, deferRendererLoad: true });
    const sampler = h.windows[0];
    const initializations = () => sampler.sent.filter(([channel, key]) => channel === "ipc" && key === "clip_init");
    assert.ok(h.activeShortcuts.has("Escape"));
    assert.equal(fs.existsSync(path.join(h.directory, "prepared.json")), false, "loading is not yet prepared");
    assert.equal(initializations().length, 0);
    await h.releaseRendererLoad();
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(h.directory, "prepared.json"), "utf8")), {
        schemaVersion: 1, sessionId: h.request.sessionId, prepared: true,
    });
    const polling = h.timers.find(timer => timer.milliseconds === 30);
    const watchdog = h.timers.find(timer => timer.milliseconds === 18_000);
    assert.ok(polling);
    polling.callback();
    assert.equal(initializations().length, 0);
    h.emit("ipc", "clip_show", []);
    h.emit("nook-initialized");
    h.emit("nook-complete", "copy", png);
    assert.equal(h.sendSync("nook-panel-open", { x: 0, y: 0, width: 200, height: 100 }).value, null);
    assert.equal(h.sendSync("nook-result-editor", { width: 800, height: 1000 }).value, null);
    assert.equal(sampler.shown, undefined, "even premature renderer show messages cannot reveal a warming helper");
    assert.equal(h.windows.length, 1);
    assert.equal(fs.existsSync(path.join(h.directory, "ready.json")), false);
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
    assert.equal(watchdog.cleared, false);
    h.writeStart();
    polling.callback();
    assert.equal(initializations().length, 1);
    assert.equal(polling.cleared, true, "fast polling is startup-only");
    assert.equal(watchdog.cleared, false, "permission to start is not readiness");
    polling.callback();
    assert.equal(initializations().length, 1, "queued polling callbacks cannot start a second capture");
    h.emit("ipc", "clip_show", []);
    assert.equal(sampler.shown, true);
    h.emit("nook-initialized");
    await h.openPanel();
    assert.equal(watchdog.cleared, true);
    assert.equal(JSON.parse(fs.readFileSync(path.join(h.directory, "ready.json"), "utf8")).ready, true);
});

test("deferred preparation and startup remain cancelled despite late load or start signals", async (t) => {
    for (const beforeLoad of [true, false]) {
        const h = await harness(t, { deferredStart: true, deferRendererLoad: beforeLoad });
        h.shortcuts.get("Escape")();
        h.writeStart();
        await h.releaseRendererLoad();
        const polling = h.timers.find(timer => timer.milliseconds === 30);
        if (polling) { assert.equal(polling.cleared, true); polling.callback(); }
        assert.equal(h.result().status, "cancelled");
        assert.equal(h.windows[0].sent.some(([channel, key]) => channel === "ipc" && key === "clip_init"), false);
        assert.equal(h.windows[0].shown, undefined);
        if (beforeLoad) assert.equal(fs.existsSync(path.join(h.directory, "prepared.json")), false);
    }
});

test("parent cancellation beats a simultaneous start without waiting for the slower watchdog", async (t) => {
    const h = await harness(t, { deferredStart: true });
    h.writeStart();
    fs.writeFileSync(path.join(h.directory, "cancel.request"), "cancel");
    const polling = h.timers.find(timer => timer.milliseconds === 30);
    polling.callback();
    assert.equal(h.result().status, "cancelled");
    assert.equal(polling.cleared, true);
    assert.equal(h.windows[0].sent.some(([channel, key]) => channel === "ipc" && key === "clip_init"), false);
});

test("deferred startup timeout, crash and invalid signals stop polling without late revival", async (t) => {
    for (const failure of ["timeout", "crash", "malformed", "wrong-session", "oversized"]) {
        const h = await harness(t, { deferredStart: true });
        const polling = h.timers.find(timer => timer.milliseconds === 30);
        if (failure === "timeout") h.timers.find(timer => timer.milliseconds === 18_000).callback();
        else if (failure === "crash") h.windows[0].webContents.emit("render-process-gone", {}, { reason: "crashed" });
        else {
            h.writeStart(failure === "malformed" ? "{" : failure === "oversized" ? "x".repeat(4097) : { schemaVersion: 1, sessionId: "0".repeat(32), start: true });
            assert.doesNotThrow(() => polling.callback(), failure);
        }
        assert.equal(h.result().status, "error", failure);
        assert.equal(polling.cleared, true, failure);
        h.writeStart();
        polling.callback();
        assert.equal(h.windows[0].sent.some(([channel, key]) => channel === "ipc" && key === "clip_init"), false, failure);
        assert.equal(h.windows[0].shown, undefined, failure);
    }
});

test("legacy startup still initializes immediately without a start file or fast polling", async (t) => {
    for (const deferredStart of [undefined, false, "true"]) {
        const h = await harness(t, { deferredStart });
        assert.equal(h.windows[0].sent.filter(([channel, key]) => channel === "ipc" && key === "clip_init").length, 1);
        assert.equal(fs.existsSync(path.join(h.directory, "prepared.json")), false);
        assert.equal(h.timers.some(timer => timer.milliseconds === 30), false);
    }
});

test("a deferred helper publishes no preparation when monitor validation or Escape registration fails", async (t) => {
    for (const failure of [{ monitorMissing: true }, { changedMonitorScale: true }, { escapeReserved: true }]) {
        const h = await harness(t, { deferredStart: true, ...failure });
        assert.equal(h.result().status, "error");
        assert.equal(fs.existsSync(path.join(h.directory, "prepared.json")), false);
        assert.equal(h.timers.some(timer => timer.milliseconds === 30), false);
        assert.ok(h.windows.every(window => !window.shown && !window.rendererLoaded));
    }
});

test("pre-start parent death and final process quit clear the fast poll and cannot revive capture", async (t) => {
    for (const quit of [false, true]) {
        const h = await harness(t, { deferredStart: true, parentProcessId: 4242, parentMissing: true });
        const polling = h.timers.find(timer => timer.milliseconds === 30);
        if (quit) h.app.emit("will-quit");
        else {
            h.timers.find(timer => timer.milliseconds === 250).callback();
            assert.equal(h.result().status, "cancelled");
        }
        assert.equal(polling.cleared, true);
        h.writeStart();
        polling.callback();
        assert.equal(h.windows[0].sent.some(([channel, key]) => channel === "ipc" && key === "clip_init"), false);
        assert.equal(h.windows[0].shown, undefined);
    }
});

test("synchronous pointer IPC commits real coordinates once, never an earlier null reply", async (t) => {
    const h = await harness(t);
    const reply = h.sendSync("ipc", "getMousePos", []);
    assert.deepEqual(JSON.parse(JSON.stringify(reply.value)), { ab: { x: -1200, y: -700 }, po: { x: 80, y: 20 }, overPanel: false });
    assert.equal(reply.writes.length, 1, "Electron only accepts one synchronous reply");
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
});

test("all synchronous compatibility queries reply once and cancelled sessions return null once", async (t) => {
    const h = await harness(t);
    const expectations = [
        ["systemLan", "zh-HANS"],
        ["runPath", h.directory],
        ["getScreens", [{ id: 1, scaleFactor: 1.5, bounds: { x: -1280, y: -720, width: 1280, height: 720 } }]],
        ["unknown-query", null],
    ];
    for (const [query, expected] of expectations) {
        const reply = h.sendSync("ipc", query, []);
        assert.deepEqual(JSON.parse(JSON.stringify(reply.value)), expected, query);
        assert.equal(reply.writes.length, 1, query);
    }
    h.shortcuts.get("Escape")();
    const cancelled = h.sendSync("ipc", "getMousePos", []);
    assert.equal(cancelled.value, null);
    assert.equal(cancelled.writes.length, 1);
});

test("one global Escape cancels a click-through session and ignores late completion", async (t) => {
    const h = await harness(t);
    const window = h.windows[0];
    h.emit("ipc", "windowIgnoreMouse", [true]);
    assert.equal(window.mouseModes.at(-1)[0], true);
    h.shortcuts.get("Escape")();
    assert.equal(h.result().status, "cancelled");
    assert.equal(window.mouseModes.at(-1)[0], false, "Escape must release click-through immediately");
    assert.equal(window.sent.filter(([name]) => name === "nook-dispose").length, 1);
    h.emit("nook-complete", "save", png);
    h.emit("nook-failure", "a late failed frame");
    h.shortcuts.get("Escape")();
    assert.equal(h.result().status, "cancelled");
    assert.equal(fs.existsSync(path.join(h.directory, "result.png")), false);
    assert.equal(window.sent.filter(([name]) => name === "nook-dispose").length, 1);
    h.emit("nook-disposed");
    assert.equal(window.isDestroyed(), true);
    assert.deepEqual(h.exits, [0]);
});

test("a single focused Escape follows the same terminal path", async (t) => {
    const h = await harness(t);
    let prevented = 0;
    h.windows[0].webContents.emit("before-input-event", { preventDefault: () => prevented++ }, { key: "Escape" });
    assert.equal(prevented, 1);
    assert.equal(h.result().status, "cancelled");
    const fallback = h.timers.find((timer) => timer.milliseconds === 350);
    assert.ok(fallback, "unresponsive renderer must not prevent the host from closing");
    fallback.callback();
    assert.equal(h.windows[0].isDestroyed(), true);
    assert.deepEqual(h.exits, [0]);
});

test("failed renderer disposal still schedules terminal cleanup and exits", async (t) => {
    const h = await harness(t);
    await h.openPanel();
    h.windows[0].webContents.send = () => { throw new Error("synthetic destroyed renderer transport"); };
    assert.doesNotThrow(() => h.emit("nook-failure", "renderer failed"));
    assert.equal(h.result().status, "error");
    const exit = h.timers.find(timer => timer.milliseconds === 350 && !timer.cleared);
    assert.ok(exit, "a broken renderer must not strand a terminal helper process");
    assert.doesNotThrow(() => exit.callback());
    assert.ok(h.windows.every(window => window.destroyed));
    assert.deepEqual(h.exits, [0]);
    assert.equal(h.activeShortcuts.size, 0);
});

test("one broken native window cleanup does not prevent destroying other surfaces or exiting", async (t) => {
    const h = await harness(t);
    await h.openPanel();
    const broken = h.windows[1];
    broken.destroy = () => { throw new Error("synthetic native destroy failure"); };
    h.windows[0].setIgnoreMouseEvents = () => { throw new Error("synthetic native input failure"); };
    assert.doesNotThrow(() => h.shortcuts.get("Escape")());
    assert.equal(h.result().status, "cancelled");
    const exit = h.timers.find(timer => timer.milliseconds === 350 && !timer.cleared);
    assert.ok(exit);
    assert.doesNotThrow(() => exit.callback());
    assert.equal(h.windows[0].destroyed, true, "input release failure must not skip window destruction");
    assert.equal(h.windows[2].destroyed, true, "another surface failure must not skip the preview");
    assert.deepEqual(h.exits, [0], "process exit is the final native cleanup fallback");
    assert.equal(h.activeShortcuts.size, 0);
});

test("forged IPC cannot export; one valid export cannot later be cancelled", async (t) => {
    const h = await harness(t);
    h.ipcMain.emit("nook-complete", { sender: { id: 999 } }, "save", png);
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
    h.emit("nook-complete", "collect", png);
    assert.equal(h.result().status, "ok");
    assert.equal(h.result().action, "collect");
    assert.deepEqual(fs.readFileSync(path.join(h.directory, "result.png")), png);
    h.shortcuts.get("Escape")();
    assert.equal(h.result().status, "ok");
    assert.equal(h.result().action, "collect");
});

test("renderer crash releases mouse passthrough and preserves an explicit error", async (t) => {
    const h = await harness(t);
    h.emit("ipc", "windowIgnoreMouse", [true]);
    h.windows[0].webContents.emit("render-process-gone", {}, { reason: "crashed" });
    assert.equal(h.result().status, "error");
    assert.match(h.result().message, /crashed/);
    assert.equal(h.windows[0].mouseModes.at(-1)[0], false);
    assert.equal(fs.existsSync(path.join(h.directory, "result.png")), false);
});

test("unavailable Escape hotkey fails closed instead of starting an uncancellable capture", async (t) => {
    const h = await harness(t, { escapeReserved: true });
    assert.equal(h.result().status, "error");
    assert.match(h.result().message, /Escape.*reserved/);
    assert.equal(h.windows[0].sent.some(([name, action]) => name === "ipc" && action === "clip_init"), false);
});

test("initialization watchdog terminates a helper that never becomes usable", async (t) => {
    const h = await harness(t);
    const timeout = h.timers.find((timer) => timer.milliseconds === 18_000);
    assert.ok(timeout);
    timeout.callback();
    assert.equal(h.result().status, "error");
    assert.match(h.result().message, /timed out/);
    assert.equal(h.windows[0].mouseModes.at(-1)[0], false);
});

test("ready handshake clears startup timeout only for the current trusted renderer", async (t) => {
    const h = await harness(t);
    const timeout = h.timers.find((timer) => timer.milliseconds === 18_000);
    h.ipcMain.emit("nook-initialized", { sender: { id: 999 } });
    assert.equal(timeout.cleared, false);
    assert.equal(fs.existsSync(path.join(h.directory, "ready.json")), false);
    h.emit("nook-initialized");
    assert.equal(timeout.cleared, false, "pixels alone are not ready until independent controls are usable");
    await h.openPanel();
    assert.equal(timeout.cleared, true);
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(h.directory, "ready.json"), "utf8")), {
        schemaVersion: 1, sessionId: h.request.sessionId, ready: true,
    });
    h.shortcuts.get("Escape")();
    h.emit("nook-initialized");
    assert.equal(h.result().status, "cancelled");
});

test("parent-side single-Escape cancellation signal closes the helper without focused input", async (t) => {
    const h = await harness(t);
    const polling = h.timers.find((timer) => timer.milliseconds === 250);
    fs.writeFileSync(path.join(h.directory, "cancel.request"), "synthetic cancel");
    polling.callback();
    assert.equal(h.result().status, "cancelled");
    assert.equal(polling.cleared, true);
    assert.equal(h.windows[0].mouseModes.at(-1)[0], false);
});

test("a dead parent cancels, but the watchdog only probes with signal zero", async (t) => {
    const h = await harness(t, { parentProcessId: 4242, parentMissing: true });
    h.timers.find((timer) => timer.milliseconds === 250).callback();
    assert.deepEqual(h.parentProbes, [[4242, 0]]);
    assert.equal(h.result().status, "cancelled");
    assert.equal(h.windows[0].mouseModes.at(-1)[0], false);
});

test("network and permission boundaries reject remote requests", async (t) => {
    const h = await harness(t);
    for (const url of ["https://example.test/upload", "http://127.0.0.1:9000/library"]) {
        let cancelled;
        h.permissions.network({ url }, (value) => { cancelled = value.cancel; });
        assert.equal(cancelled, true);
    }
    for (const url of ["file:///capture.html", "data:image/png;base64,AA==", "blob:file:///image"]) {
        let cancelled;
        h.permissions.network({ url }, (value) => { cancelled = value.cancel; });
        assert.equal(cancelled, false);
    }
    assert.equal(h.permissions.check(), false);
    let granted;
    h.permissions.request({}, "clipboard-read", (value) => { granted = value; });
    assert.equal(granted, false);
    assert.deepEqual({ ...h.windows[0].openHandler() }, { action: "deny" });
});

test("result editor becomes a movable bounded window on its source monitor, not a fullscreen overlay", async (t) => {
    const h = await harness(t, { transactionalOutputs: true });
    const panel = await h.openPanel();
    const bounds = h.sendSync("nook-result-editor", { width: 800, height: 18000 }).value;
    assert.ok(bounds.width <= 1060 && bounds.height <= 680);
    assert.ok(bounds.x >= -1280 && bounds.y >= -720);
    assert.ok(bounds.x + bounds.width <= 0 && bounds.y + bounds.height <= -40);
    const window = h.windows[0];
    assert.equal(panel.destroyed, true, "native capture controls must not remain above the result editor");
    assert.equal(h.windows[2].destroyed, true, "pure-image preview closes with toolbar");
    assert.equal(window.movable, true);
    assert.equal(window.resizable, true);
    assert.equal(window.alwaysOnTop, false);
    assert.equal(window.skipTaskbar, false);
    assert.equal(window.focusable, true);
    assert.equal(window.focused, true);
    assert.equal(window.contentProtection, false, "finished editor is an ordinary capturable window, not a protected live overlay");
    assert.equal(h.shortcutCalls.at(-1), "unregisterAll", "normal result window must not steal Escape from unrelated apps");
    assert.equal(h.sendSync("nook-result-editor", { width: 800, height: 1000 }).value, null, "late resize request cannot reposition an editor the user moved");
});

test("sampling overlay is capture-protected before renderer load and unavailable protection fails closed", async (t) => {
    const h = await harness(t);
    assert.equal(h.windows[0].protectedBeforeRendererLoad, true);
    assert.equal(h.windows[0].contentProtection, true);
    const unprotected = await harness(t, { samplerProtectionUnavailable: true });
    assert.equal(unprotected.result().status, "error");
    assert.match(unprotected.result().message, /截图.*遮罩|遮罩.*截图/);
    assert.equal(unprotected.windows[0].rendererLoaded, undefined, "no capture code starts without compositor exclusion");
    assert.equal(unprotected.windows[0].shown, undefined);
    assert.equal(unprotected.windows[0].sent.some(([name, action]) => name === "ipc" && action === "clip_init"), false);
});

test("unchanged native surface state does not re-show windows, resize them, or reload the same preview image", async (t) => {
    const h = await harness(t);
    const toolbar = await h.openPanel(), sampler = h.windows[0], preview = h.windows[2];
    const state = { status: "800 × 1200 px", title: "完整长图", finishEnabled: true,
        imageWidth: 800, imageHeight: 1200, preview: `data:image/png;base64,${png.toString("base64")}` };
    h.emit("nook-panel-update", state);
    h.emit("ipc", "clip_show", []);
    const counts = () => [toolbar.boundsChanges.length, preview.boundsChanges.length,
        toolbar.inactiveShowCount ?? 0, preview.inactiveShowCount ?? 0, sampler.inactiveShowCount ?? 0,
        preview.elements.get("image").srcWrites.length];
    const before = counts();
    for (let i = 0; i < 20; i++) {
        h.emit("nook-panel-update", state);
        h.emit("ipc", "clip_show", []);
    }
    assert.deepEqual(counts(), before, "unchanged state must be compositor-idempotent");
    assert.equal(preview.elements.get("image").srcWrites.length, 1);
    h.emit("nook-panel-update", { ...state, preview: "data:image/png;base64,AA==" });
    assert.equal(preview.elements.get("image").srcWrites.length, 2, "a genuinely new thumbnail still appears");
    assert.equal(preview.inactiveShowCount, before[3], "new pixels do not raise an already visible window again");
    const previewBoundsCount = preview.boundsChanges.length;
    h.emit("nook-panel-update", { ...state, imageHeight: 18000 });
    assert.equal(preview.boundsChanges.length, previewBoundsCount + 1, "a changed aspect ratio still reflows its preview");
});

test("independent panel is natively interactive before movement, and the first real button action reaches the host", async (t) => {
    const h = await harness(t);
    const panel = await h.openPanel();
    const sampler = h.windows[0];
    assert.equal(panel.shown, true);
    assert.equal(panel.inactiveShow, true, "opening controls must not steal keyboard focus from the page being scrolled");
    assert.equal(panel.contentProtection, true);
    assert.equal(panel.configuration.frame, false);
    assert.equal(panel.configuration.hasShadow, false);
    assert.equal(panel.configuration.movable, false, "toolbar stays attached below source selection");
    assert.equal(sampler.configuration.focusable, false, "transparent sampling layer must not steal PageDown/scroll keys from the source");
    h.emit("ipc", "clip_show", []);
    assert.equal(sampler.inactiveShow, true);
    assert.deepEqual(panel.mouseModes, [[false]], "native hit testing never depends on renderer hover/polling");
    for (const ignore of [false, true, false]) h.emit("ipc", "windowIgnoreMouse", [ignore]);
    assert.ok(sampler.mouseModes.every(([ignore]) => ignore === true), "legacy IPC cannot steal or forward the first panel click");
    assert.equal(panel.elements.get("copy").disabled, true);
    h.emit("nook-panel-update", { status: "800 × 2400 px", title: "已拼接", finishEnabled: true, preview: `data:image/png;base64,${png.toString("base64")}` });
    assert.equal(panel.elements.get("status").textContent, "800 × 2400 px");
    assert.equal(panel.elements.get("copy").disabled, false);
    // Invoke the actual script's click listener, with no prior mouse movement
    // or activation message. This traverses panel IPC -> production host.
    panel.elements.get("copy").click();
    panel.elements.get("copy").click();
    assert.equal(sampler.sent.filter(([name]) => name === "nook-panel-finish").length, 1);
    assert.equal(panel.elements.get("copy").disabled, true);
    assert.equal(sampler.sent.find(([name]) => name === "nook-panel-finish")[1], "copy", "the checkmark requests copy, never implicitly opens an editor");
    assert.deepEqual(panel.mouseModes, [[false]], "pending completion never makes controls click-through");
    h.emit("nook-panel-update", { status: "late live frame", title: "", finishEnabled: true });
    panel.elements.get("copy").click();
    assert.equal(sampler.sent.filter(([name]) => name === "nook-panel-finish").length, 1, "a queued live update must not re-enable a pending finish");
    h.emit("nook-panel-update", { status: "请停止滚动后重试", title: "", finishEnabled: true, resume: true });
    panel.elements.get("copy").click();
    assert.equal(sampler.sent.filter(([name]) => name === "nook-panel-finish").length, 2, "explicit retry can finish again");
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false, "finish waits for renderer final pixels and parent output acknowledgement");
    panel.elements.get("cancel").click();
    assert.equal(h.result().status, "cancelled");
    assert.equal(panel.destroyed, true);
});

test("native panel pointer filtering tracks actual moved bounds in DIP coordinates", async (t) => {
    const h = await harness(t);
    const panel = await h.openPanel();
    const initial = panel.getBounds();
    assert.equal(initial.x, -879);
    assert.equal(initial.y, -712);
    assert.ok(initial.x >= -1280 && initial.x + initial.width <= 0);
    panel.setBounds({ x: -1200, y: -700, width: 248, height: 460 });
    const inside = h.sendSync("ipc", "getMousePos", []).value;
    assert.equal(inside.overPanel, true, "renderer must not use stale initial panel bounds after native drag");
    assert.deepEqual({ ...inside.po }, { x: 80, y: 20 });
    h.screen.getCursorScreenPoint = () => ({ x: -952, y: -700 });
    assert.equal(h.sendSync("ipc", "getMousePos", []).value.overPanel, false, "right edge is outside native bounds");
});

test("panel IPC rejects forged actions, remote previews, oversized previews, and late reactivation", async (t) => {
    const h = await harness(t);
    h.ipcMain.emit("nook-panel-open", { sender: { id: 999 } }, { x: 10, y: 10, width: 248, height: 460 });
    assert.equal(h.windows.length, 1);
    const panel = await h.openPanel();
    for (const preview of ["https://example.test/track.png", "data:image/svg+xml;base64,AA==", "data:image/png;base64," + "A".repeat(8_000_000)]) {
        h.emit("nook-panel-update", { status: "forged", title: "", finishEnabled: true, preview });
        assert.notEqual(panel.elements.get("status").textContent, "forged");
    }
    h.emit("nook-panel-update", { status: "ready", title: "", finishEnabled: true });
    for (const id of [41, 43, 999]) h.ipcMain.emit("nook-panel-action", { sender: { id } }, "copy");
    assert.equal(h.windows[0].sent.some(([name]) => name === "nook-panel-finish"), false);
    panel.elements.get("cancel").click();
    h.emit("nook-panel-update", { status: "late", title: "", finishEnabled: true });
    assert.equal(h.sendSync("nook-panel-open", { x: 10, y: 10, width: 248, height: 460 }).value, null);
    assert.equal(h.windows.length, 3);
});

test("native control keyboard Escape and loading failures share terminal cleanup", async (t) => {
    const h = await harness(t);
    const panel = await h.openPanel();
    let prevented = false;
    panel.documentEvents.get("keydown")({ key: "Escape", preventDefault: () => { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(h.result().status, "cancelled");
    assert.equal(panel.destroyed, true);
    const failed = await harness(t, { panelLoadFails: true });
    await failed.openPanel();
    assert.equal(failed.result().status, "error");
    assert.match(failed.result().message, /控制面板/);
    assert.equal(failed.windows[1].destroyed, true);
    assert.equal(fs.existsSync(path.join(failed.directory, "ready.json")), false);
    const unprotected = await harness(t, { protectionUnavailable: true });
    await unprotected.openPanel();
    assert.equal(unprotected.result().status, "error");
    assert.equal(unprotected.windows[1].destroyed, true);
    assert.equal(unprotected.windows[1].shown, undefined, "a panel that cannot exclude itself is never shown over a crop");
});

function exportReply(h, id, status, overrides = {}) {
    fs.writeFileSync(path.join(h.directory, `export-${id}.reply.json`), JSON.stringify({ schemaVersion: 1, sessionId: h.request.sessionId, exportId: id, status, ...overrides }));
    h.timers.find((timer) => timer.milliseconds === 250).callback();
}

test("a committed output acknowledgement wins over a renderer failure before the next poll", async t => {
    for (const action of ["copy", "save", "pin", "collect"]) {
        for (const failure of ["sampler", "toolbar", "preview"]) {
            const h = await harness(t, { transactionalOutputs: true });
            await h.openPanel();
            h.sendSync("nook-result-ready", { width: 800, height: 2400 });
            h.emit("nook-complete", action, png);
            fs.writeFileSync(path.join(h.directory, "export-1.reply.json"), JSON.stringify({
                schemaVersion: 1, sessionId: h.request.sessionId, exportId: 1, status: "ok",
            }));
            if (failure === "sampler") h.windows[0].webContents.emit("render-process-gone", {}, { reason: "crashed" });
            else h.windows[failure === "toolbar" ? 1 : 2].webContents.emit("unresponsive");
            assert.equal(h.result().status, "ok", `${action}/${failure}: a late view failure cannot undo confirmed output`);
            assert.equal(h.result().action, action);
            assert.equal(h.result().exportId, 1);
            assert.equal(h.result().imagePath, "export-1.png");
            assert.deepEqual(fs.readFileSync(path.join(h.directory, "export-1.png")), png);
            assert.equal(h.windows[0].sent.filter(([name]) => name === "nook-dispose").length, 1);
            assert.equal(h.windows[1].destroyed, true);
            assert.equal(h.windows[2].destroyed, true);
            h.timers.find(timer => timer.milliseconds === 250).callback();
            assert.equal(h.result().status, "ok", "a delayed polling callback cannot rewrite the terminal result");
        }
    }
});

test("a renderer failure never upgrades missing, rejected or untrusted output acknowledgement to success", async t => {
    for (const reply of [undefined, "{", { status: "cancelled" }, { status: "error" },
        { status: "ok", sessionId: "0".repeat(32) }, { status: "ok", exportId: 2 }]) {
        const h = await harness(t, { transactionalOutputs: true });
        h.emit("nook-complete", "save", png);
        if (reply !== undefined) fs.writeFileSync(path.join(h.directory, "export-1.reply.json"),
            typeof reply === "string" ? reply : JSON.stringify({ schemaVersion: 1, sessionId: h.request.sessionId, exportId: 1, ...reply }));
        h.windows[0].webContents.emit("render-process-gone", {}, { reason: "crashed" });
        assert.equal(h.result().status, "error");
        assert.equal(Object.hasOwn(h.result(), "action"), false);
        assert.equal(Object.hasOwn(h.result(), "imagePath"), false);
        assert.deepEqual(fs.readFileSync(path.join(h.directory, "export-1.png")), png, "uncertain output remains recoverable");
        assert.equal(h.windows[0].sent.filter(([name]) => name === "nook-dispose").length, 1);
    }
});

test("a lost parent with an already committed acknowledgement still completes successfully", async t => {
    const h = await harness(t, { transactionalOutputs: true, parentProcessId: 4242, parentMissing: true });
    h.emit("nook-complete", "save", png);
    exportReply(h, 1, "ok");
    assert.equal(h.result().status, "ok");
    assert.equal(h.result().action, "save");
    assert.equal(h.result().exportId, 1);
});

test("cancelled save and failed output preserve the same editor; only confirmed success closes it", async (t) => {
    const h = await harness(t, { transactionalOutputs: true });
    const window = h.windows[0];
    h.emit("nook-complete", "save", png);
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
    assert.equal(window.sent.some(([name]) => name === "nook-dispose"), false);
    h.emit("nook-complete", "copy", png);
    assert.equal(fs.existsSync(path.join(h.directory, "export-2.json")), false, "double clicking output does not start another export");
    exportReply(h, 1, "cancelled");
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
    assert.equal(window.sent.at(-1)[0], "nook-export-reply");
    assert.equal(window.sent.at(-1)[1], "cancelled");
    h.emit("nook-complete", "copy", png);
    exportReply(h, 2, "error", { message: "clipboard busy" });
    assert.equal(window.sent.at(-1)[2], "clipboard busy");
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
    h.emit("nook-complete", "collect", png);
    exportReply(h, 3, "ok");
    assert.equal(h.result().status, "ok");
    assert.equal(h.result().exportId, 3);
    assert.equal(h.result().imagePath, "export-3.png");
    assert.equal(window.sent.at(-1)[0], "nook-dispose");
});

test("stale export acknowledgement cannot commit this editing session", async (t) => {
    const h = await harness(t, { transactionalOutputs: true });
    h.emit("nook-complete", "copy", png);
    exportReply(h, 1, "ok", { sessionId: "00000000000000000000000000000000" });
    assert.equal(h.result().status, "error");
    assert.match(h.result().message, /does not belong/);
});

test("Escape during pending output defers cancellation so a committed output keeps its success", async (t) => {
    const h = await harness(t, { transactionalOutputs: true });
    h.emit("nook-complete", "copy", png);
    h.shortcuts.get("Escape")();
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false, "do not misreport cancellation while clipboard output may commit");
    exportReply(h, 1, "ok");
    assert.equal(h.result().status, "ok");
    assert.equal(h.result().exportId, 1);
    assert.equal(h.windows[0].sent.filter(([name]) => name === "nook-dispose").length, 1);
});

test("explicit pending-output cancellation dismisses every surface immediately without guessing the output result", async (t) => {
    for (const mode of ["ready", "editor"]) {
        for (const action of ["copy", "save", "pin", "collect"]) {
            for (const reply of ["ok", "cancelled", "error"]) {
                const h = await harness(t, { transactionalOutputs: true, parentProcessId: 4242 });
                const toolbar = await h.openPanel();
                h.emit("nook-initialized");
                h.emit("nook-panel-update", { status: "ready", title: "", finishEnabled: true,
                    preview: `data:image/png;base64,${png.toString("base64")}` });
                toolbar.elements.get("copy").click();
                h.sendSync(mode === "ready" ? "nook-result-ready" : "nook-result-editor", { width: 800, height: 2400 });
                h.emit("nook-complete", action, png);
                const surfaces = h.windows.filter(window => !window.destroyed);
                if (mode === "ready" && action !== "save") h.shortcuts.get("Escape")();
                else if (action === "save") {
                    // This is an explicit session cancel, not Escape owned by
                    // the parent's native Save dialog.
                    fs.writeFileSync(path.join(h.directory, "cancel.request"), "cancel");
                    h.timers.find(timer => timer.milliseconds === 250).callback();
                } else h.windows[0].webContents.emit("before-input-event", { preventDefault() {} }, { key: "Escape" });
                const witness = `${mode}/${action}/${reply}`;
                assert.ok(surfaces.every(window => window.shown === false), `${witness}: one cancellation releases the screen immediately`);
                assert.equal(h.activeShortcuts.has("Escape"), false, `${witness}: Escape belongs to other apps again`);
                assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false, "pending output is neither falsely cancelled nor assumed successful");
                assert.equal(h.exits.length, 0, "the existing acknowledgement is still observed");

                const showCounts = surfaces.map(window => [window.showCount, window.inactiveShowCount]);
                h.emit("nook-panel-update", { status: "late frame", title: "", finishEnabled: true, resume: true });
                h.emit("ipc", "clip_show", []);
                h.timers.find(timer => timer.milliseconds === 1200).callback();
                h.emit("nook-complete", "copy", png);
                const poll = h.timers.find(timer => timer.milliseconds === 250);
                for (let i = 0; i < 2400; i++) poll.callback();
                assert.ok(surfaces.every(window => window.shown === false), `${witness}: late events cannot reopen a cancelled capture`);
                assert.deepEqual(surfaces.map(window => [window.showCount, window.inactiveShowCount]), showCounts);
                assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false, "polling does not introduce an arbitrary output timeout");
                assert.equal(fs.existsSync(path.join(h.directory, "export-2.json")), false, "uncertain output is never retried");
                assert.deepEqual(fs.readFileSync(path.join(h.directory, "export-1.png")), png);

                exportReply(h, 1, reply);
                assert.equal(h.result().status, reply === "ok" ? "ok" : "cancelled", witness);
                if (reply === "ok") {
                    assert.equal(h.result().action, action);
                    assert.equal(h.result().exportId, 1);
                }
                assert.deepEqual(surfaces.map(window => [window.showCount, window.inactiveShowCount]), showCounts,
                    "negative acknowledgements do not reopen an already-dismissed editor or preview");
                assert.equal(h.windows[0].sent.some(([name]) => name === "nook-export-reply"), false);
                h.timers.find(timer => timer.milliseconds === 350).callback();
                assert.deepEqual(h.exits, [0]);
            }
        }
    }
});

test("pending-output cancellation also suppresses late native panel load callbacks", async (t) => {
    const h = await harness(t, { transactionalOutputs: true });
    h.sendSync("nook-panel-open", { x: 0, y: 0, width: 100, height: 100 });
    h.emit("nook-complete", "copy", png);
    h.emit("nook-cancel");
    for (let i = 0; i < 6; i++) await Promise.resolve();
    assert.ok(h.windows.every(window => window.shown === false), "a deferred ready callback must not show an already-dismissed surface");
    assert.equal(h.activeShortcuts.has("Escape"), false);
    exportReply(h, 1, "ok");
    assert.equal(h.result().status, "ok");
});

test("native pure-image preview uses complete-image scaling, not a card or scrolling tail", async (t) => {
    const h = await harness(t);
    const toolbar = await h.openPanel({ selection: { x: 80, y: 20, width: 800 / 1.5, height: 1000 / 1.5 } });
    const preview = h.windows[2];
    assert.equal(preview.contentProtection, true);
    assert.equal(preview.configuration.frame, false);
    assert.equal(preview.configuration.hasShadow, false);
    assert.equal(preview.configuration.focusable, false);
    assert.doesNotMatch(preview.html, /<button|<header|<footer|overflow:auto|scrollTop/);
    const firstToolbar = toolbar.getBounds();
    h.emit("nook-panel-update", { status: "800 × 1200 px", title: "完整长图", finishEnabled: true,
        imageWidth: 800, imageHeight: 1200, preview: `data:image/png;base64,${png.toString("base64")}` });
    const firstPreview = preview.getBounds();
    assert.equal(preview.shown, true);
    assert.equal(preview.elements.get("image").src, `data:image/png;base64,${png.toString("base64")}`);
    h.emit("nook-panel-update", { status: "800 × 18000 px", title: "完整长图", finishEnabled: true, imageWidth: 800, imageHeight: 18000 });
    const longPreview = preview.getBounds();
    assert.ok(longPreview.width < firstPreview.width);
    assert.equal(longPreview.y + longPreview.height, firstPreview.y + firstPreview.height);
    assert.deepEqual(toolbar.getBounds(), firstToolbar);
    h.screen.getCursorScreenPoint = () => ({ x: longPreview.x + 1, y: longPreview.y + 1 });
    assert.equal(h.sendSync("ipc", "getMousePos", []).value.overPanel, true, "input over pure-image window is excluded from source sampling");
});

test("each actual native toolbar action forwards its explicit intent; only edit requests editor", async (t) => {
    for (const action of ["edit", "save", "copy"]) {
        const h = await harness(t);
        const toolbar = await h.openPanel();
        h.emit("nook-panel-update", { status: "ready", title: "", finishEnabled: true });
        toolbar.elements.get(action).click();
        assert.equal(h.windows[0].sent.find(([name]) => name === "nook-panel-finish")[1], action);
        assert.equal(h.windows[0].focused, undefined, "click alone does not force a fullscreen editor");
    }
});

test("result-ready freezes only sampler, preserves controls and global one-Escape cancellation", async (t) => {
    const h = await harness(t);
    const toolbar = await h.openPanel();
    const sampler = h.windows[0];
    const ready = h.sendSync("nook-result-ready", { width: 800, height: 2400 });
    assert.deepEqual({ ...ready.value }, { ready: true });
    assert.equal(ready.writes.length, 1);
    assert.equal(sampler.shown, false);
    assert.equal(toolbar.destroyed, false);
    assert.equal(h.windows[2].destroyed, false);
    assert.equal(h.activeShortcuts.has("Escape"), true);
    h.emit("ipc", "clip_show", []);
    assert.equal(sampler.shown, false, "legacy show cannot resurrect fullscreen prepared scene");
    h.shortcuts.get("Escape")();
    assert.equal(h.result().status, "cancelled");
    assert.equal(toolbar.destroyed, true);
    assert.equal(h.windows[2].destroyed, true);
});

test("direct Save gives native dialog Escape and hides topmost controls; cancelled Save restores only ready view", async (t) => {
    const h = await harness(t, { transactionalOutputs: true });
    const toolbar = await h.openPanel();
    const sampler = h.windows[0], preview = h.windows[2];
    h.emit("nook-panel-update", { status: "ready", title: "", finishEnabled: true, preview: `data:image/png;base64,${png.toString("base64")}` });
    const beforeSave = { toolbarShows: toolbar.inactiveShowCount, previewShows: preview.inactiveShowCount,
        previewWrites: preview.elements.get("image").srcWrites.length };
    toolbar.elements.get("save").click();
    h.sendSync("nook-result-ready", { width: 800, height: 2400 });
    h.emit("nook-complete", "save", png);
    assert.equal(h.activeShortcuts.has("Escape"), false, "Escape now cancels the Save dialog, not the capture");
    assert.equal(toolbar.shown, false);
    assert.equal(preview.shown, false);
    assert.equal(toolbar.elements.get("cancel").disabled, true);
    h.emit("nook-panel-update", { status: "late frame", title: "", finishEnabled: true, resume: true });
    assert.equal(toolbar.elements.get("copy").disabled, true, "queued resume cannot bypass pending export");
    assert.equal(preview.shown, false, "late frame cannot show topmost image over Save dialog");
    exportReply(h, 1, "cancelled");
    assert.equal(h.activeShortcuts.has("Escape"), true);
    assert.equal(toolbar.shown, true);
    assert.equal(preview.shown, true);
    assert.equal(toolbar.inactiveShowCount, beforeSave.toolbarShows + 1, "cancelled Save re-shows the previously hidden toolbar once");
    assert.equal(preview.inactiveShowCount, beforeSave.previewShows + 1, "cancelled Save re-shows the previously hidden image once");
    assert.equal(preview.elements.get("image").srcWrites.length, beforeSave.previewWrites, "retained image is not reloaded during Save cancellation");
    assert.equal(toolbar.elements.get("cancel").disabled, false, "host acknowledgement restores Cancel without waiting for renderer resume");
    assert.equal(sampler.shown, false);
    assert.equal(sampler.focused, undefined);
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
    h.emit("nook-panel-update", { status: "已取消保存，图片已保留", title: "", finishEnabled: true, resume: true });
    assert.equal(toolbar.elements.get("copy").disabled, false);
    toolbar.elements.get("copy").click();
    h.emit("nook-complete", "copy", png);
    exportReply(h, 2, "ok");
    assert.equal(h.result().status, "ok");
    assert.equal(h.result().action, "copy");
});

test("verified rejected output restores all retained-result controls without renderer resume", async (t) => {
    for (const status of ["cancelled", "error"]) {
        for (const retry of ["edit", "save", "copy"]) {
            await t.test(`${status} then ${retry}`, async (t) => {
                const h = await harness(t, { transactionalOutputs: true });
                const toolbar = await h.openPanel(), sampler = h.windows[0], preview = h.windows[2];
                h.emit("nook-panel-update", { status: "ready", title: "", finishEnabled: true,
                    preview: `data:image/png;base64,${png.toString("base64")}` });
                toolbar.elements.get("save").click();
                h.sendSync("nook-result-ready", { width: 800, height: 2400 });
                h.emit("nook-panel-update", { status: "选择保存位置…", title: "", finishEnabled: false });
                h.emit("nook-complete", "save", png);
                const waiting = h.timers.find(timer => timer.milliseconds === 1200);
                for (const id of ["edit", "save", "copy", "cancel"])
                    assert.equal(toolbar.elements.get(id).disabled, true, `${id}: unacknowledged output cannot be retried`);
                sampler.rendererIPC.on("nook-export-reply", () => {
                    assert.equal(toolbar.elements.get(retry).disabled, true,
                        "renderer reset is sent before another native toolbar action can be queued");
                });
                exportReply(h, 1, status, { message: "synthetic save failure" });
                // Do not simulate the hidden renderer sending resume: the
                // validated destination reply must restore the native panel.
                for (const id of ["edit", "save", "copy", "cancel"])
                    assert.equal(toolbar.elements.get(id).disabled, false, `${id}: verified ${status} must restore controls without resume`);
                assert.equal(waiting.cleared, true);
                const restoredStatus = toolbar.elements.get("status").textContent;
                waiting.callback();
                assert.equal(toolbar.elements.get("status").textContent, restoredStatus, "an already queued wait hint cannot overwrite restored state");
                assert.equal(toolbar.shown, true);
                assert.equal(preview.shown, true);
                assert.equal(sampler.shown, false);
                assert.equal(sampler.focused, undefined);
                assert.equal(h.activeShortcuts.has("Escape"), true);
                assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
                assert.equal(fs.existsSync(path.join(h.directory, "export-2.json")), false, "restoring controls never retries output itself");
                assert.deepEqual(fs.readFileSync(path.join(h.directory, "export-1.png")), png);
                toolbar.elements.get(retry).click();
                const replyIndex = sampler.sent.findIndex(([channel]) => channel === "nook-export-reply");
                const retryIndex = sampler.sent.findLastIndex(([channel]) => channel === "nook-panel-finish");
                assert.ok(replyIndex >= 0 && retryIndex > replyIndex, "retry uses the same renderer queue after its state reset");
                assert.equal(sampler.sent[retryIndex][1], retry);
                if (retry === "edit") {
                    assert.ok(h.sendSync("nook-result-editor", { width: 800, height: 2400 }).value);
                    assert.equal(sampler.shown, true);
                    assert.equal(toolbar.destroyed, true);
                    assert.equal(preview.destroyed, true);
                } else {
                    h.emit("nook-complete", retry, png);
                    exportReply(h, 2, "ok");
                    assert.equal(h.result().status, "ok");
                    assert.equal(h.result().action, retry);
                    assert.equal(h.result().exportId, 2);
                    h.emit("nook-panel-update", { status: "late resume", title: "", finishEnabled: true, resume: true });
                    h.emit("nook-complete", "copy", png);
                    assert.equal(fs.existsSync(path.join(h.directory, "export-3.json")), false, "confirmed success cannot be retried");
                    assert.equal(toolbar.destroyed, true);
                    assert.equal(preview.destroyed, true);
                }
            });
        }
    }
});

test("cancelled direct Save can enter a normal unprotected result editor without reviving capture surfaces", async (t) => {
    const h = await harness(t, { transactionalOutputs: true });
    const toolbar = await h.openPanel(), sampler = h.windows[0], preview = h.windows[2];
    h.emit("nook-panel-update", { status: "ready", title: "", finishEnabled: true,
        imageWidth: 800, imageHeight: 2400, preview: `data:image/png;base64,${png.toString("base64")}` });
    toolbar.elements.get("save").click();
    h.sendSync("nook-result-ready", { width: 800, height: 2400 });
    h.emit("nook-complete", "save", png);
    exportReply(h, 1, "cancelled");
    h.emit("nook-panel-update", { status: "已取消保存，图片已保留", title: "", finishEnabled: true, resume: true });
    toolbar.elements.get("edit").click();
    assert.equal(sampler.sent.at(-1)[0], "nook-panel-finish");
    assert.equal(sampler.sent.at(-1)[1], "edit");
    assert.ok(h.sendSync("nook-result-editor", { width: 800, height: 2400 }).value);
    assert.equal(sampler.contentProtection, false);
    assert.equal(sampler.shown, true);
    assert.equal(sampler.focused, true);
    assert.deepEqual(sampler.mouseModes.at(-1), [false]);
    assert.equal(toolbar.destroyed, true);
    assert.equal(preview.destroyed, true);
    assert.equal(h.activeShortcuts.has("Escape"), false);
    h.emit("nook-panel-update", { status: "late capture", title: "", finishEnabled: true, resume: true });
    assert.equal(h.windows.length, 3, "late capture messages cannot create new native controls");
    assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false, "cancelled destination does not discard the editable image");
});

test("failed ready output stays retryable without exposing or focusing the fullscreen scene", async (t) => {
    const h = await harness(t, { transactionalOutputs: true });
    const toolbar = await h.openPanel();
    h.sendSync("nook-result-ready", { width: 800, height: 2400 });
    h.emit("nook-complete", "copy", png);
    exportReply(h, 1, "error", { message: "clipboard temporarily busy" });
    assert.equal(h.windows[0].shown, false);
    assert.equal(h.windows[0].focused, undefined);
    assert.equal(toolbar.shown, true);
    assert.equal(h.activeShortcuts.has("Escape"), true);
    assert.equal(h.windows[0].sent.at(-1)[2], "clipboard temporarily busy");
});

test("deferred parent cancellation keeps polling and closes only after cancelled or failed output acknowledgement", async (t) => {
    for (const status of ["cancelled", "error"]) {
        const h = await harness(t, { transactionalOutputs: true });
        h.emit("nook-complete", "save", png);
        fs.writeFileSync(path.join(h.directory, "cancel.request"), "cancel");
        h.timers.find(timer => timer.milliseconds === 250).callback();
        assert.equal(fs.existsSync(path.join(h.directory, "result.json")), false);
        exportReply(h, 1, status);
        assert.equal(h.result().status, "cancelled");
        assert.equal(h.windows[0].sent.filter(([name]) => name === "nook-dispose").length, 1);
    }
});

test("a parent lost during output yields recoverable uncertainty rather than false cancellation or an endless wait", async (t) => {
    const h = await harness(t, { transactionalOutputs: true, parentProcessId: 4242, parentMissing: true });
    h.emit("nook-complete", "copy", png);
    h.timers.find(timer => timer.milliseconds === 250).callback();
    assert.equal(h.result().status, "error");
    assert.match(h.result().message, /导出结果尚未确认/);
    assert.deepEqual(fs.readFileSync(path.join(h.directory, "export-1.png")), png);
});

test("ready state and preview dimensions reject forged or oversized input without hiding sampler", async (t) => {
    const h = await harness(t);
    const toolbar = await h.openPanel();
    for (const dimensions of [{ width: NaN, height: 2 }, { width: 0, height: 1 }, { width: 8000, height: 10000 }, { width: 32769, height: 1 }]) {
        assert.equal(h.sendSync("nook-result-ready", dimensions).value, null);
        h.emit("nook-panel-update", { status: "rejected", title: "", finishEnabled: true, imageWidth: dimensions.width, imageHeight: dimensions.height });
        assert.notEqual(toolbar.elements.get("status").textContent, "rejected");
    }
    h.ipcMain.emit("nook-result-ready", { sender: { id: 999 } }, { width: 1, height: 1 });
    assert.equal(h.windows[0].hideCount, undefined);
});

test("brief failure and slow-operation hints preserve the four button screen positions and never cross monitor bounds", async (t) => {
    const h = await harness(t);
    const toolbar = await h.openPanel();
    const base = toolbar.getBounds();
    assert.equal(toolbar.elements.get("status").hidden, true);
    h.emit("nook-panel-update", { status: "未找到连续内容，请慢慢向回滚动", title: "请重试", finishEnabled: true });
    const noticeBounds = toolbar.getBounds();
    assert.equal(toolbar.elements.get("status").hidden, false);
    assert.equal(noticeBounds.y + parseInt(toolbar.elements.get("toolbar").style.top, 10), base.y);
    assert.equal(noticeBounds.x, base.x);
    assert.ok(noticeBounds.y >= -712 && noticeBounds.y + noticeBounds.height <= -48);
    h.emit("nook-panel-update", { status: "800 × 2400 px", title: "", finishEnabled: true });
    assert.equal(toolbar.elements.get("status").hidden, true);
    assert.deepEqual(toolbar.getBounds(), base);
    toolbar.elements.get("copy").click();
    assert.equal(toolbar.elements.get("status").hidden, true, "fast normal action has no flashing status card");
    h.timers.find(timer => timer.milliseconds === 1200).callback();
    assert.equal(toolbar.elements.get("status").hidden, false, "a slow operation has visible feedback");
    h.emit("nook-panel-update", { status: "ready", title: "", finishEnabled: true, resume: true });
    assert.equal(toolbar.elements.get("status").hidden, true);
});

test("fullscreen cap warning remains visible when preview begins four DIP below the top-clamped toolbar", async (t) => {
    const h = await harness(t, { region: { x: -1920, y: -1080, width: 1920, height: 1080 } });
    const toolbar = await h.openPanel(), preview = h.windows[2];
    const update = { status: "1600 × 2332 px", title: "完整长图", finishEnabled: true,
        imageWidth: 1600, imageHeight: 2332, preview: `data:image/png;base64,${png.toString("base64")}` };
    h.emit("nook-panel-update", update);
    const base = toolbar.getBounds(), image = preview.getBounds();
    assert.equal(image.y - (base.y + base.height), 4, "exact formerly hidden-warning geometry");
    h.emit("nook-panel-update", { ...update, status: "已达到大小上限，已保留当前内容；可编辑、保存或复制" });
    assert.equal(toolbar.elements.get("status").hidden, false);
    const controls = toolbar.getBounds(), moved = preview.getBounds();
    assert.equal(controls.y + parseInt(toolbar.elements.get("toolbar").style.top, 10), base.y);
    assert.equal(controls.x, base.x);
    assert.ok(moved.x + moved.width <= controls.x - 12);
    assert.equal(moved.y + moved.height, image.y + image.height);
    assert.equal(moved.width, image.width);
    assert.equal(moved.height, image.height);
    assert.ok(moved.x >= -1272 && moved.y >= -712);
    assert.ok(controls.y + controls.height <= -48);
});
