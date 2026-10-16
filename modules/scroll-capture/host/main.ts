import { app, BrowserWindow, globalShortcut, ipcMain, nativeImage, screen, session } from "electron";
import fs from "node:fs";
import path from "node:path";
import { readRequest, validatePng, writeReady, writeResult, type CaptureAction, type CaptureRequest } from "./protocol.mjs";
import defaults from "./nook-defaults";

let request: CaptureRequest;
let terminal = false;
let stopping = false;
let win: BrowserWindow | null = null;
let startupTimer: ReturnType<typeof setTimeout> | undefined;
let exitTimer: ReturnType<typeof setTimeout> | undefined;
let cancellationTimer: ReturnType<typeof setInterval> | undefined;
const argumentIndex = process.argv.indexOf("--nook-request");
try {
    if (argumentIndex < 0) throw new Error("--nook-request is required");
    request = readRequest(process.argv[argumentIndex + 1]);
    const profile = path.join(request.outputDirectory, "host-profile");
    fs.mkdirSync(profile, { recursive: true });
    app.setPath("userData", profile);
    app.setPath("sessionData", profile);
    app.setName("Nook Scrolling Capture");
} catch (error) {
    console.error(error);
    app.exit(2);
}

function stop() {
    if (stopping) return;
    stopping = true;
    clearTimeout(startupTimer);
    clearInterval(cancellationTimer);
    if (app.isReady()) globalShortcut.unregisterAll();
    if (win && !win.isDestroyed()) {
        win.setIgnoreMouseEvents(false);
        win.webContents.send("nook-dispose");
    }
    exitTimer = setTimeout(exit, 350);
}

function exit() {
    clearTimeout(exitTimer);
    if (app.isReady()) globalShortcut.unregisterAll();
    for (const window of BrowserWindow.getAllWindows()) {
        window.setIgnoreMouseEvents(false);
        window.destroy();
    }
    app.exit(0);
}

function finish(status: "ok" | "cancelled" | "error", options: { action?: CaptureAction; png?: Buffer; message?: string } = {}) {
    if (terminal) return;
    terminal = true;
    try {
        writeResult(request, status, options);
    } catch (error) {
        console.error("Unable to commit scrolling capture result", error);
        try { writeResult(request, "error", { message: String(error) }); }
        catch (writeError) { console.error(writeError); }
    } finally {
        stop();
    }
}

function trusted(event: Electron.IpcMainEvent) {
    return Boolean(win && !win.isDestroyed() && event.sender.id === win.webContents.id);
}

function getSetting(key: string): unknown {
    if (!key || key.split(".").some((part) => ["__proto__", "prototype", "constructor"].includes(part))) return null;
    let value: unknown = defaults;
    for (const part of key.split(".")) value = value && typeof value === "object" ? (value as Record<string, unknown>)[part] : undefined;
    return value ?? null;
}

ipcMain.on("store", (event, data) => {
    if (!trusted(event)) { event.returnValue = null; return; }
    if (data?.type === "get") event.returnValue = getSetting(data.path);
    else if (data?.type === "getAll") event.returnValue = defaults;
    else if (data?.type === "set" && typeof data.path === "string") {
        const parts: string[] = data.path.split(".");
        if (parts.some((part) => ["__proto__", "prototype", "constructor"].includes(part))) return;
        let object = defaults as unknown as Record<string, unknown>;
        for (const part of parts.slice(0, -1)) {
            if (!object[part] || typeof object[part] !== "object") return;
            object = object[part] as Record<string, unknown>;
        }
        object[parts.at(-1)!] = data.value;
    } else event.returnValue = null;
});

ipcMain.on("nook-request", (event) => {
    event.returnValue = trusted(event) && !terminal ? request : null;
});
ipcMain.on("nook-disposed", (event) => { if (trusted(event) && terminal) exit(); });
ipcMain.on("nook-cancel", (event) => { if (trusted(event)) finish("cancelled"); });
ipcMain.on("nook-failure", (event, message) => { if (trusted(event)) finish("error", { message: String(message) }); });
ipcMain.on("nook-initialized", (event) => {
    if (!trusted(event) || terminal) return;
    try { writeReady(request); clearTimeout(startupTimer); }
    catch (error) { finish("error", { message: String(error) }); }
});
ipcMain.on("nook-complete", (event, action, raw) => {
    if (!trusted(event) || terminal) return;
    try {
        const png = Buffer.isBuffer(raw) ? raw : Buffer.from(raw);
        const dimensions = validatePng(png);
        const image = nativeImage.createFromBuffer(png);
        if (image.isEmpty() || image.getSize().width !== dimensions.width || image.getSize().height !== dimensions.height)
            throw new Error("The scrolling image could not be decoded");
        finish("ok", { action, png });
    } catch (error) { finish("error", { message: String(error) }); }
});

ipcMain.on("ipc", (event, key, args) => {
    if (!trusted(event) || terminal) { event.returnValue = null; return; }
    // Electron's returnValue setter sends the synchronous IPC reply at once.
    // Do not send a placeholder null before computing the actual query value:
    // a second assignment cannot replace the first reply received upstream.
    let reply: unknown = null;
    switch (key) {
        case "clip_show": win?.show(); break;
        case "clip_close":
        case "windowClose": finish("cancelled"); break;
        case "windowIgnoreMouse": win?.setIgnoreMouseEvents(Boolean(args?.[0]), { forward: true }); break;
        case "getMousePos": {
            const point = screen.getCursorScreenPoint();
            const bounds = win!.getBounds();
            reply = { ab: point, po: { x: point.x - bounds.x, y: point.y - bounds.y } };
            break;
        }
        case "systemLan": reply = "zh-HANS"; break;
        case "runPath": reply = path.resolve(__dirname, "../.."); break;
        case "getScreens": reply = screen.getAllDisplays(); break;
        case "dialogMessage": finish("error", { message: String(args?.[0]?.message ?? "Screen capture initialization failed") }); reply = 0; break;
        case "clip_hide": break;
        default: break;
    }
    event.returnValue = reply;
});

app.on("will-quit", () => globalShortcut.unregisterAll());
app.on("before-quit", () => { if (!terminal) finish("cancelled"); });
process.on("uncaughtException", (error) => { console.error(error); if (request) finish("error", { message: String(error) }); else app.exit(2); });
process.on("unhandledRejection", (error) => { console.error(error); if (request) finish("error", { message: String(error) }); else app.exit(2); });

app.whenReady().then(async () => {
    if (terminal || !request) return;
    cancellationTimer = setInterval(() => {
        if (terminal) return;
        if (fs.existsSync(path.join(request.outputDirectory, "cancel.request"))) { finish("cancelled"); return; }
        if (request.parentProcessId) {
            try { process.kill(request.parentProcessId, 0); }
            catch (error) { if ((error as NodeJS.ErrnoException).code === "ESRCH") finish("cancelled"); }
        }
    }, 250);
    const native = require("node-screenshots") as typeof import("node-screenshots");
    const monitor = native.Monitor.all().find((m) => m.x() === request.monitor.x && m.y() === request.monitor.y && m.width() === request.monitor.width && m.height() === request.monitor.height);
    if (!monitor) throw new Error("The selected monitor changed. Please start a new screenshot.");
    const center = screen.screenToDipPoint({ x: request.monitor.x + Math.floor(request.monitor.width / 2), y: request.monitor.y + Math.floor(request.monitor.height / 2) });
    const display = screen.getDisplayNearestPoint(center);
    if (Math.abs(display.scaleFactor - request.monitor.scaleFactor) > 0.01 ||
        Math.abs(display.bounds.width * display.scaleFactor - request.monitor.width) > 2 ||
        Math.abs(display.bounds.height * display.scaleFactor - request.monitor.height) > 2)
        throw new Error("Monitor scaling changed. Please start a new screenshot.");

    session.defaultSession.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
    session.defaultSession.setPermissionCheckHandler(() => false);
    session.defaultSession.webRequest.onBeforeRequest((details, callback) => callback({ cancel: !/^(file:|data:|blob:|devtools:)/i.test(details.url) }));
    win = new BrowserWindow({
        title: "Nook · 滚动截屏", ...display.bounds,
        show: false, transparent: true, frame: false, resizable: false, movable: false,
        fullscreenable: false, skipTaskbar: true, alwaysOnTop: true, hasShadow: false,
        autoHideMenuBar: true,
        webPreferences: { nodeIntegration: true, contextIsolation: false, sandbox: false, backgroundThrottling: false, zoomFactor: 1 },
    });
    win.setMenu(null);
    win.on("page-title-updated", (event) => event.preventDefault());
    win.setAlwaysOnTop(true, "screen-saver");
    win.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
    win.webContents.on("will-navigate", (event) => event.preventDefault());
    win.webContents.on("before-input-event", (event, input) => {
        if (input.key === "Escape") { event.preventDefault(); finish("cancelled"); }
    });
    win.webContents.on("render-process-gone", (_event, details) => finish("error", { message: `Screenshot renderer stopped: ${details.reason}` }));
    win.webContents.on("unresponsive", () => finish("error", { message: "Screenshot editor stopped responding" }));
    win.on("close", (event) => { if (!stopping) { event.preventDefault(); finish("cancelled"); } });
    if (!globalShortcut.register("Escape", () => finish("cancelled")))
        throw new Error("Escape is already reserved by another application. Scrolling capture was not started.");
    startupTimer = setTimeout(() => finish("error", { message: "Screenshot editor initialization timed out" }), 18_000);
    await win.loadFile(path.join(__dirname, "../renderer/capture.html"));
    if (terminal || !win || win.isDestroyed()) return;
    win.webContents.send("ipc", "clip_init", [[{ ...display, id: monitor.id() }], undefined, monitor.id(), undefined]);
}).catch((error) => finish("error", { message: String(error) }));
