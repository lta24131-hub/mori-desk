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
    if (options.parentProcessId) request.parentProcessId = options.parentProcessId;
    fs.writeFileSync(requestPath, JSON.stringify(request));
    const app = new EventEmitter();
    const exits = [];
    app.setPath = () => {};
    app.setName = () => {};
    app.exit = (code) => exits.push(code);
    app.whenReady = () => Promise.resolve();
    app.isReady = () => true;
    const ipcMain = new EventEmitter();
    const shortcuts = new Map();
    const shortcutCalls = [];
    const globalShortcut = {
        register(key, callback) { shortcuts.set(key, callback); return !options.escapeReserved; },
        unregisterAll() { shortcutCalls.push("unregisterAll"); },
    };
    const windows = [];
    class BrowserWindow extends EventEmitter {
        constructor(configuration) {
            super(); this.configuration = configuration; this.destroyed = false;
            this.mouseModes = []; this.sent = [];
            this.webContents = new EventEmitter();
            this.webContents.id = 41;
            this.webContents.send = (...args) => this.sent.push(args);
            this.webContents.setWindowOpenHandler = (callback) => { this.openHandler = callback; };
            windows.push(this);
        }
        isDestroyed() { return this.destroyed; }
        setIgnoreMouseEvents(...args) { this.mouseModes.push(args); }
        setMenu() {}
        setAlwaysOnTop() {}
        show() { this.shown = true; }
        getBounds() { return this.configuration; }
        loadFile() { return Promise.resolve(); }
        destroy() { this.destroyed = true; }
        static getAllWindows() { return windows.filter((window) => !window.destroyed); }
    }
    const display = { id: 1, scaleFactor: 1.5, bounds: { x: -1280, y: -720, width: 1280, height: 720 } };
    const screen = {
        screenToDipPoint: (point) => ({ x: point.x / 1.5, y: point.y / 1.5 }),
        getDisplayNearestPoint: () => display,
        getAllDisplays: () => [display],
        getCursorScreenPoint: () => ({ x: -1200, y: -700 }),
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
    processFake.argv = ["Nook.ScrollCapture.exe", "--nook-request", requestPath];
    const parentProbes = [];
    processFake.kill = (pid, signal) => {
        parentProbes.push([pid, signal]);
        if (options.parentMissing) throw Object.assign(new Error("missing synthetic parent"), { code: "ESRCH" });
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
            if (name === "./nook-defaults") return { default: { test: { value: 1 } } };
            if (name === "node-screenshots") return { Monitor: { all: () => [monitor] } };
            if (name === "node:fs" || name === "node:path") return require(name);
            throw new Error(`Unexpected runtime dependency in headless host: ${name}`);
        },
    });
    vm.runInContext(javascript, context, { filename: "production-scroll-capture-host.js" });
    for (let i = 0; i < 6; i++) await Promise.resolve();
    const event = { sender: { id: 41 } };
    return {
        directory, request, windows, ipcMain, event, shortcuts, shortcutCalls, timers, exits, errors, permissions, parentProbes,
        result: () => JSON.parse(fs.readFileSync(path.join(directory, "result.json"), "utf8")),
        emit: (name, ...args) => ipcMain.emit(name, event, ...args),
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

test("synchronous pointer IPC commits real coordinates once, never an earlier null reply", async (t) => {
    const h = await harness(t);
    const reply = h.sendSync("ipc", "getMousePos", []);
    assert.deepEqual(JSON.parse(JSON.stringify(reply.value)), { ab: { x: -1200, y: -700 }, po: { x: 80, y: 20 } });
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
