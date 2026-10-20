import { app, BrowserWindow, globalShortcut, ipcMain, nativeImage, screen, session } from "electron";
import fs from "node:fs";
import path from "node:path";
import { readRequest, readWarmRequest, readWarmActivation, writeWarmReady, writeStartupTiming, validatePng, writeReady, writePrepared, readStartRequest, writeResult, writeExportRequest, readExportReply, allowsAction, type CaptureAction, type CaptureRequest, type WarmRequest } from "./protocol.mjs";
import defaults from "./nook-defaults";
import panelIcons from "./nook-panel-icons";
import { layoutCaptureControls, type CaptureRect } from "./panel-layout.mjs";

let request: CaptureRequest;
let warm: WarmRequest | undefined;
let identity: CaptureRequest | WarmRequest;
const startedAt = Date.now();
const timings: Record<string, number | boolean> = {};
let timingWritten = false;
let warmTimer: ReturnType<typeof setTimeout> | undefined;
let warmPollingTimer: ReturnType<typeof setInterval> | undefined;
let rendererLoaded = false;
let rendererScriptReady = false;
let warmReady = false;
let warmActivationPrepared = false;
let terminal = false;
let stopping = false;
let win: BrowserWindow | null = null;
let panelWin: BrowserWindow | null = null;
let previewWin: BrowserWindow | null = null;
let panelReady = false;
let previewReady = false;
let captureSelection: CaptureRect | null = null;
let captureWorkArea: CaptureRect | null = null;
let panelStarted = false;
let rendererReady = false;
let panelFinishPending = false;
let panelWaitTimer: ReturnType<typeof setTimeout> | undefined;
let showPanelWaitNotice = false;
type PanelState = { status: string; title: string; finishEnabled: boolean; preview?: string; imageWidth?: number; imageHeight?: number; busy?: boolean };
let panelState: PanelState = { status: "准备第一屏…", title: "在原页面滚动；Esc 取消整次截图", finishEnabled: false };
let startupTimer: ReturnType<typeof setTimeout> | undefined;
let startupPollingTimer: ReturnType<typeof setInterval> | undefined;
let captureStarted = false;
let exitTimer: ReturnType<typeof setTimeout> | undefined;
let cancellationTimer: ReturnType<typeof setInterval> | undefined;
let exportSequence = 0;
let pendingExport: { id: number; action: CaptureAction; png: Buffer } | null = null;
let resultEditor = false;
let resultReady = false;
let captureEscapeRegistered = false;
let cancelAfterExport = false;
const argumentIndex = process.argv.indexOf("--nook-request");
const warmArgumentIndex = process.argv.indexOf("--nook-warm");
try {
    if ((argumentIndex < 0) === (warmArgumentIndex < 0)) throw new Error("Exactly one of --nook-request or --nook-warm is required");
    if (warmArgumentIndex >= 0) identity = warm = readWarmRequest(process.argv[warmArgumentIndex + 1]);
    else identity = request = readRequest(process.argv[argumentIndex + 1]);
    timings.warm = Boolean(warm);
    const profile = path.join(identity.outputDirectory, "host-profile");
    fs.mkdirSync(profile, { recursive: true });
    app.setPath("userData", profile);
    app.setPath("sessionData", profile);
    app.setName("Nook Scrolling Capture");
    if (warm) warmTimer = setTimeout(() => {
        if (request || terminal) return;
        // A complete request published before expiry wins even while the
        // renderer is loading. Its own fresh startup deadline now applies.
        pollWarmActivation();
        if (!request && !terminal) finish("cancelled");
    }, Math.max(1, 120_000 - (Date.now() - startedAt)));
} catch (error) {
    terminal = true;
    console.error(error);
    app.exit(2);
}

function phase(name: string) { timings[name] = Math.max(0, Date.now() - startedAt); }
function commitTiming() {
    if (timingWritten || !identity) return;
    timingWritten = true;
    try { writeStartupTiming(identity, timings); } catch { /* Diagnostics cannot affect capture. */ }
}

function tryTerminalCleanup(action: () => void) {
    try { action(); }
    catch (error) { console.error("Scrolling capture cleanup failed", error); }
}

function stop() {
    if (stopping) return;
    stopping = true;
    // Arm the process fallback before talking to a possibly crashed renderer
    // or broken native window. None of those failures may strand the helper.
    exitTimer = setTimeout(exit, 350);
    clearTimeout(startupTimer);
    clearTimeout(warmTimer);
    clearInterval(warmPollingTimer);
    clearInterval(startupPollingTimer);
    clearTimeout(panelWaitTimer);
    clearInterval(cancellationTimer);
    tryTerminalCleanup(() => { if (app.isReady()) globalShortcut.unregisterAll(); });
    captureEscapeRegistered = false;
    tryTerminalCleanup(closePanel);
    if (win && !win.isDestroyed()) {
        const capture = win;
        tryTerminalCleanup(() => capture.setIgnoreMouseEvents(false));
        tryTerminalCleanup(() => capture.webContents.send("nook-dispose"));
    }
}

function exit() {
    terminal = true;
    clearInterval(startupPollingTimer);
    clearTimeout(exitTimer);
    try {
        tryTerminalCleanup(() => { if (app.isReady()) globalShortcut.unregisterAll(); });
        for (const window of BrowserWindow.getAllWindows()) {
            tryTerminalCleanup(() => window.setIgnoreMouseEvents(false));
            tryTerminalCleanup(() => window.destroy());
        }
    } finally { app.exit(0); }
}

function finish(status: "ok" | "cancelled" | "error", options: { action?: CaptureAction; png?: Buffer; message?: string; exportId?: number } = {}) {
    if (terminal) return;
    if (!request) {
        // Ordinary screenshots may discard their unused warm renderer without
        // producing a capture result or a user-visible failure.
        terminal = true;
        commitTiming();
        stop();
        return;
    }
    // A view can fail in the short interval between the parent committing an
    // output and our next reply poll. Its failure must not overwrite that
    // already-confirmed success and make Nook reopen the original screenshot.
    // Missing, malformed or rejected acknowledgements never imply success.
    if (status === "error" && pendingExport) {
        const pending = pendingExport;
        try {
            if (readExportReply(request, pending.id)?.status === "ok") {
                status = "ok";
                options = { action: pending.action, png: pending.png, exportId: pending.id };
            }
        } catch { /* Preserve the original failure and recoverable export. */ }
    }
    // An output request may already be committing in the parent. Never write
    // a contradictory terminal cancellation before its acknowledgement.
    if (status === "cancelled" && pendingExport) {
        if (cancelAfterExport) return;
        cancelAfterExport = true;
        clearTimeout(panelWaitTimer);
        showPanelWaitNotice = false;
        // Dismiss immediately even if the parent is still busy, but keep only
        // the existing transaction alive until its acknowledgement arrives.
        // Hiding is not cancellation of an output that may already be saved.
        tryTerminalCleanup(suspendCaptureEscape);
        for (const surface of [win, panelWin, previewWin]) {
            if (surface && !surface.isDestroyed()) tryTerminalCleanup(() => surface.hide());
        }
        return;
    }
    terminal = true;
    commitTiming();
    try {
        writeResult(request, status, options);
    } catch (error) {
        console.error("Unable to commit scrolling capture result", error);
        try { writeResult(request, "error", { message: String(error) }); }
        catch (writeError) { console.error(writeError); }
    } finally {
        pendingExport = null;
        stop();
    }
}

function trusted(event: Electron.IpcMainEvent) {
    return Boolean(win && !win.isDestroyed() && event.sender.id === win.webContents.id);
}

function suspendCaptureEscape() {
    if (!captureEscapeRegistered) return;
    globalShortcut.unregisterAll();
    captureEscapeRegistered = false;
}

function restoreCaptureEscape() {
    if (!request || terminal || cancelAfterExport || resultEditor || captureEscapeRegistered) return;
    if (!globalShortcut.register("Escape", () => finish("cancelled")))
        throw new Error("Escape is already reserved by another application. Scrolling capture was not started.");
    captureEscapeRegistered = true;
}

function validResultDimensions(value: { width?: number; height?: number } | undefined) {
    return Boolean(value && Number.isSafeInteger(value.width) && Number.isSafeInteger(value.height) &&
        value.width! > 0 && value.height! > 0 && value.width! <= 32768 && value.height! <= 32768 && value.width! * value.height! <= 64000000);
}

function closePanel() {
    const panel = panelWin;
    const preview = previewWin;
    panelWin = null;
    previewWin = null;
    panelReady = false;
    previewReady = false;
    panelFinishPending = false;
    clearTimeout(panelWaitTimer);
    showPanelWaitNotice = false;
    panelState = { status: "", title: "", finishEnabled: false };
    if (panel && !panel.isDestroyed()) panel.destroy();
    if (preview && !preview.isDestroyed()) preview.destroy();
}

function markReady() {
    if (!captureStarted || !rendererReady || !panelReady || !previewReady || terminal) return;
    try { writeReady(request); clearTimeout(startupTimer); phase("readyMs"); commitTiming(); }
    catch (error) { finish("error", { message: String(error) }); }
}

// Two protected native surfaces: image only beside the selection, controls
// below it. Neither surface changes mouse hit testing during sampling.
function previewDocument() {
    return `<!doctype html><html lang="zh-CN"><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
<title>Nook · 长图预览</title><style>html,body{width:100%;height:100%;margin:0;overflow:hidden;background:transparent}img{display:block;width:100%;height:100%;object-fit:contain}img:not([src]){display:none}</style>
<body><img id="image" alt="完整长截图预览"><script>
const ipc=require('electron').ipcRenderer, image=document.getElementById('image');
let currentPreview;
ipc.on('nook-panel-state',(_event,state)=>{if(state.preview&&state.preview!==currentPreview){currentPreview=state.preview;image.src=state.preview;}});
</script></body></html>`;
}

function panelDocument() {
    return `<!doctype html><html lang="zh-CN"><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
<title>Nook · 长截图工具</title><style>
*{box-sizing:border-box}html,body{width:100%;height:100%;margin:0;overflow:hidden;background:transparent;color:#62666c;font:13px 'Segoe UI','Microsoft YaHei UI',sans-serif}
#toolbar{position:absolute;left:0;right:0;top:0;height:48px;display:flex;align-items:center;justify-content:space-evenly;padding:5px 8px;background:#fff;border:1px solid #e7e7e7;border-radius:8px}
button{display:grid;place-items:center;width:40px;height:36px;flex:none;border:0;border-radius:5px;background:transparent;color:inherit;cursor:pointer}button:hover{background:#f0f1f2}button:focus-visible{outline:2px solid #777;outline-offset:-2px}button:disabled{opacity:.35;cursor:default}button span{width:24px;height:24px;background:currentColor;mask:var(--icon) center/contain no-repeat;-webkit-mask:var(--icon) center/contain no-repeat;pointer-events:none}#cancel{color:#e44b4b}#copy{color:#18ac67}
#status{position:absolute;left:2px;right:2px;height:22px;line-height:22px;padding:0 7px;background:#fff7e6;color:#6b4b22;border-radius:4px;font-size:11px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}#status[hidden]{display:none}
</style><body><div id="toolbar"><button id="edit" aria-label="编辑长截图" title="编辑长截图" disabled><span style="--icon:url('${panelIcons.edit}')"></span></button><button id="save" aria-label="保存长截图" title="保存长截图" disabled><span style="--icon:url('${panelIcons.save}')"></span></button><button id="cancel" aria-label="取消截图" title="取消截图（Esc）"><span style="--icon:url('${panelIcons.cancel}')"></span></button><button id="copy" aria-label="完成并复制" title="完成并复制" disabled><span style="--icon:url('${panelIcons.copy}')"></span></button></div><span id="status" role="status" aria-live="polite" hidden></span>
<script>
const ipc=require('electron').ipcRenderer;
const ids=['edit','save','copy'], buttons=ids.map(id=>document.getElementById(id)), cancel=document.getElementById('cancel'), status=document.getElementById('status'), toolbar=document.getElementById('toolbar');
ipc.on('nook-panel-state',(_event,state)=>{status.textContent=state.status;status.title=state.title||state.status;status.hidden=!state.notice;status.style.top=(state.noticeTop||0)+'px';toolbar.style.top=(state.toolbarTop||0)+'px';buttons.forEach(button=>{button.disabled=!state.finishEnabled;});cancel.disabled=Boolean(state.busy);});
ids.forEach((id,index)=>{buttons[index].onclick=()=>{if(buttons[index].disabled)return;buttons.forEach(button=>{button.disabled=true;});ipc.send('nook-panel-action',id);};});
cancel.onclick=()=>ipc.send('nook-panel-action','cancel');
document.addEventListener('keydown',event=>{if(event.key==='Escape'){event.preventDefault();ipc.send('nook-panel-action','cancel');}});
</script></body></html>`;
}

function setBoundsIfChanged(surface: BrowserWindow, bounds: CaptureRect) {
    const current = surface.getBounds();
    if (current.x !== bounds.x || current.y !== bounds.y || current.width !== bounds.width || current.height !== bounds.height)
        surface.setBounds(bounds);
}

function showInactiveIfHidden(surface: BrowserWindow | null) {
    if (surface && !surface.isDestroyed() && !surface.isVisible()) surface.showInactive();
}

function publishPanel() {
    if (terminal || cancelAfterExport) return;
    let previewBounds: CaptureRect | undefined;
    if (panelReady && panelWin && captureSelection && captureWorkArea) {
        const layout = layoutCaptureControls({ selection: captureSelection, workArea: captureWorkArea,
            imageWidth: panelState.imageWidth ?? captureSelection.width, imageHeight: panelState.imageHeight ?? captureSelection.height });
        const needsNotice = /未完成|未找到|失败|无法|请停止|仍在变化|已取消|上限|限制/.test(panelState.status) || (showPanelWaitNotice && panelFinishPending);
        const base = layout.toolbar;
        previewBounds = layout.preview;
        let bounds = base, toolbarTop = 0, noticeTop = 0, notice = false;
        if (needsNotice) {
            for (const above of [true, false]) {
                const expanded = { ...base, y: base.y - (above ? 26 : 0), height: base.height + 26 };
                const image = layout.preview;
                const overlaps = panelState.preview && expanded.x < image.x + image.width && expanded.x + expanded.width > image.x &&
                    expanded.y < image.y + image.height && expanded.y + expanded.height > image.y;
                if (expanded.y >= captureWorkArea.y + 8 && expanded.y + expanded.height <= captureWorkArea.y + captureWorkArea.height - 8) {
                    if (overlaps) {
                        // Important errors/cap warnings never disappear just
                        // because a long preview starts beside the toolbar.
                        // Yield horizontal image space, not button positions.
                        const leftSpace = expanded.x - 12 - (captureWorkArea.x + 8);
                        const rightSpace = captureWorkArea.x + captureWorkArea.width - 8 - (expanded.x + expanded.width + 12);
                        const useLeft = leftSpace >= rightSpace;
                        const available = Math.max(1, useLeft ? leftSpace : rightSpace);
                        const width = Math.min(image.width, Math.floor(available));
                        const height = Math.max(1, Math.floor(image.height * width / image.width));
                        previewBounds = { x: Math.round(useLeft ? expanded.x - 12 - width : expanded.x + expanded.width + 12),
                            y: image.y + image.height - height, width, height };
                    }
                    bounds = expanded; toolbarTop = above ? 26 : 0; noticeTop = above ? 0 : 52; notice = true; break;
                }
            }
        }
        setBoundsIfChanged(panelWin, bounds);
        panelWin.webContents.send("nook-panel-state", { ...panelState, finishEnabled: panelState.finishEnabled && !pendingExport, busy: Boolean(pendingExport), notice, toolbarTop, noticeTop });
    }
    if (previewReady && previewWin) {
        previewWin.webContents.send("nook-panel-state", panelState);
        if (panelState.preview && captureSelection && captureWorkArea) {
            const layout = layoutCaptureControls({ selection: captureSelection, workArea: captureWorkArea,
                imageWidth: panelState.imageWidth ?? captureSelection.width, imageHeight: panelState.imageHeight ?? captureSelection.height });
            setBoundsIfChanged(previewWin, previewBounds ?? layout.preview);
            if (pendingExport?.action !== "save") showInactiveIfHidden(previewWin);
        }
    }
}

function configurePanelWindow(panel: BrowserWindow) {
    panel.setMenu(null);
    panel.setIgnoreMouseEvents(false);
    panel.setContentProtection(true);
    if (!panel.isContentProtected()) throw new Error("当前系统无法排除截图控制面板，请重新截图。");
    panel.setAlwaysOnTop(true, "screen-saver");
    panel.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
    panel.webContents.on("will-navigate", (navigation) => navigation.preventDefault());
    panel.webContents.on("before-input-event", (inputEvent, input) => {
        if (input.key === "Escape") { inputEvent.preventDefault(); finish("cancelled"); }
    });
    panel.webContents.on("render-process-gone", () => finish("error", { message: "滚动截屏预览意外关闭，请重新截图。" }));
    panel.webContents.on("unresponsive", () => finish("error", { message: "滚动截屏预览停止响应，请重新截图。" }));
    panel.on("close", (closeEvent) => {
        if ((panelWin === panel || previewWin === panel) && !stopping) { closeEvent.preventDefault(); finish("cancelled"); }
    });
}

ipcMain.on("nook-panel-open", (event, value) => {
    if (!trusted(event) || !captureStarted || terminal || cancelAfterExport || resultEditor || resultReady || panelStarted) { event.returnValue = null; return; }
    const rect = value?.selection ?? value;
    if (!rect || ![rect.x, rect.y, rect.width, rect.height].every(Number.isFinite) || rect.width < 1 || rect.height < 1) {
        event.returnValue = null; return;
    }
    try {
        const source = win!.getBounds();
        const display = screen.getDisplayMatching(source);
        captureWorkArea = display.workArea;
        // The parent request is authoritative; convert its physical selection
        // using the source monitor origin, not global-coordinate division.
        captureSelection = { x: source.x + (request.region.x - request.monitor.x) / display.scaleFactor,
            y: source.y + (request.region.y - request.monitor.y) / display.scaleFactor,
            width: request.region.width / display.scaleFactor, height: request.region.height / display.scaleFactor };
        const layout = layoutCaptureControls({ selection: captureSelection, workArea: captureWorkArea });
        const options = { show: false, frame: false, transparent: true, resizable: false, movable: false,
            fullscreenable: false, skipTaskbar: true, alwaysOnTop: true, hasShadow: false, backgroundColor: "#00000000", autoHideMenuBar: true,
            webPreferences: { nodeIntegration: true, contextIsolation: false, sandbox: false, backgroundThrottling: false, zoomFactor: 1 } };
        const panel = new BrowserWindow({ ...options, title: "Nook · 长截图工具", ...layout.toolbar });
        panelWin = panel;
        panelStarted = true;
        configurePanelWindow(panel);
        const preview = new BrowserWindow({ ...options, title: "Nook · 长图预览", ...layout.preview, focusable: false });
        previewWin = preview;
        configurePanelWindow(preview);
        void panel.loadURL(`data:text/html;charset=utf-8,${encodeURIComponent(panelDocument())}`).then(() => {
            if (panelWin !== panel || terminal || panel.isDestroyed()) return;
            panelReady = true;
            publishPanel();
            if (!cancelAfterExport) showInactiveIfHidden(panel);
            markReady();
        }).catch((error) => finish("error", { message: `无法打开滚动截屏控制面板：${String(error)}` }));
        void preview.loadURL(`data:text/html;charset=utf-8,${encodeURIComponent(previewDocument())}`).then(() => {
            if (previewWin !== preview || terminal || preview.isDestroyed()) return;
            previewReady = true;
            publishPanel();
            markReady();
        }).catch((error) => finish("error", { message: `无法打开滚动截屏控制面板：${String(error)}` }));
        win!.setIgnoreMouseEvents(true);
        event.returnValue = layout;
    } catch (error) {
        event.returnValue = null;
        finish("error", { message: `无法打开滚动截屏控制面板：${String(error)}` });
    }
});
ipcMain.on("nook-panel-update", (event, value) => {
    if (!trusted(event) || terminal || cancelAfterExport || resultEditor || !panelWin || !value ||
        typeof value.status !== "string" || value.status.length > 240 || typeof value.title !== "string" || value.title.length > 500 ||
        typeof value.finishEnabled !== "boolean") return;
    // Only a bounded local PNG thumbnail crosses the IPC boundary. Text never
    // becomes HTML, and a forged URL cannot make the preview load a website.
    if (value.preview !== undefined && (typeof value.preview !== "string" || value.preview.length > 8_000_000 ||
        !/^data:image\/png;base64,[A-Za-z0-9+/]+=*$/.test(value.preview))) return;
    if ((value.imageWidth !== undefined || value.imageHeight !== undefined) &&
        (!Number.isSafeInteger(value.imageWidth) || !Number.isSafeInteger(value.imageHeight) || value.imageWidth < 1 || value.imageHeight < 1 ||
         value.imageWidth > 32768 || value.imageHeight > 32768 || value.imageWidth * value.imageHeight > 64000000)) return;
    if (value.resume === true && !pendingExport) { panelFinishPending = false; clearTimeout(panelWaitTimer); showPanelWaitNotice = false; }
    panelState = { status: value.status, title: value.title, finishEnabled: value.finishEnabled && !panelFinishPending,
        preview: value.preview ?? panelState.preview, imageWidth: value.imageWidth ?? panelState.imageWidth, imageHeight: value.imageHeight ?? panelState.imageHeight };
    publishPanel();
});
ipcMain.on("nook-panel-close", (event) => { if (trusted(event)) closePanel(); });
ipcMain.on("nook-panel-action", (event, action) => {
    if (terminal || resultEditor || !panelWin || panelWin.isDestroyed() || event.sender.id !== panelWin.webContents.id) return;
    if (action === "cancel") { finish("cancelled"); return; }
    if (!["edit", "save", "copy"].includes(action) || !panelState.finishEnabled || panelFinishPending || pendingExport) return;
    panelFinishPending = true;
    clearTimeout(panelWaitTimer);
    showPanelWaitNotice = false;
    panelWaitTimer = setTimeout(() => { if (!terminal && panelFinishPending) { showPanelWaitNotice = true; publishPanel(); } }, 1200);
    panelState = { ...panelState, finishEnabled: false, status: "正在确认最后一屏…" };
    publishPanel();
    win?.webContents.send("nook-panel-finish", action);
});

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
    event.returnValue = trusted(event) && !terminal && captureStarted ? request : null;
});
ipcMain.on("nook-renderer-loaded", (event) => {
    if (!trusted(event) || terminal) return;
    rendererScriptReady = true;
    pollWarmActivation();
});
ipcMain.on("nook-disposed", (event) => { if (trusted(event) && terminal) exit(); });
ipcMain.on("nook-cancel", (event) => { if (trusted(event)) finish("cancelled"); });
ipcMain.on("nook-failure", (event, message) => { if (trusted(event)) finish("error", { message: String(message) }); });
ipcMain.on("nook-initialized", (event) => {
    if (!trusted(event) || !captureStarted || terminal) return;
    rendererReady = true;
    markReady();
});
ipcMain.on("nook-complete", (event, action, raw) => {
    if (!trusted(event) || !captureStarted || terminal || pendingExport) return;
    try {
        // Reject disabled destinations before copying or decoding a long PNG.
        if (!allowsAction(request, action)) throw new Error("此截图输出功能已关闭。");
        const png = Buffer.isBuffer(raw) ? raw : Buffer.from(raw);
        const dimensions = validatePng(png);
        const image = nativeImage.createFromBuffer(png);
        if (image.isEmpty() || image.getSize().width !== dimensions.width || image.getSize().height !== dimensions.height)
            throw new Error("The scrolling image could not be decoded");
        if (request.transactionalOutputs) {
            const id = exportSequence + 1;
            // The parent can open its native Save dialog as soon as the file
            // appears. Escape must belong to that dialog before publication.
            if (action === "save") {
                suspendCaptureEscape();
                panelWin?.hide();
                previewWin?.hide();
            }
            writeExportRequest(request, id, action, png);
            exportSequence = id;
            pendingExport = { id, action, png };
            publishPanel();
        } else finish("ok", { action, png });
    } catch (error) {
        // Recoverable export failures must not destroy an otherwise healthy editor.
        try { restoreCaptureEscape(); } catch (hotkeyError) { finish("error", { message: String(hotkeyError) }); return; }
        win?.webContents.send("nook-export-reply", "error", String(error));
        if (!resultEditor) { publishPanel(); showInactiveIfHidden(panelWin); }
    }
});

function pollExport() {
    const pending = pendingExport;
    if (!pending || terminal) return;
    try {
        const reply = readExportReply(request, pending.id);
        if (!reply) return;
        if (reply.status === "ok") {
            finish("ok", { action: pending.action, png: pending.png, exportId: pending.id });
        } else {
            pendingExport = null;
            if (cancelAfterExport) { cancelAfterExport = false; finish("cancelled"); return; }
            restoreCaptureEscape();
            win?.webContents.send("nook-export-reply", reply.status, reply.message);
            if (resultEditor) { win?.show(); win?.focus(); }
            else if (resultReady) {
                // A verified negative destination reply makes the retained
                // result retryable. Do not wait for the hidden sampler to
                // echo resume before enabling its separate native controls.
                // Its export reset is queued above, before any new action.
                panelFinishPending = false;
                clearTimeout(panelWaitTimer);
                showPanelWaitNotice = false;
                const status = reply.status === "cancelled" ? "已取消保存，图片已保留；可重试、编辑或复制。" : "未完成，图片已保留；可重试、编辑或复制。";
                panelState = { ...panelState, status, title: reply.message || status, finishEnabled: true };
                publishPanel();
                showInactiveIfHidden(panelWin);
            }
        }
    } catch (error) {
        // A corrupt response is not permission to retry a possibly committed
        // output. Fail closed, leaving the on-disk exported image recoverable.
        finish("error", { message: String(error) });
    }
}

ipcMain.on("nook-result-ready", (event, dimensions) => {
    if (!trusted(event) || !captureStarted || terminal || resultEditor || pendingExport || !validResultDimensions(dimensions)) {
        event.returnValue = null; return;
    }
    resultReady = true;
    // A finalized image is not an editor. Keep only the image and compact
    // controls visible while Save/Copy runs, including cancelled Save retries.
    win!.hide();
    event.returnValue = { ready: true };
});

ipcMain.on("nook-result-editor", (event, dimensions) => {
    if (!trusted(event) || !captureStarted || terminal || resultEditor || pendingExport) { event.returnValue = null; return; }
    if (!validResultDimensions(dimensions)) {
        event.returnValue = null; return;
    }
    const display = screen.getDisplayMatching(win!.getBounds());
    const area = display.workArea;
    const width = Math.min(area.width, Math.max(520, Math.min(1060, dimensions.width / display.scaleFactor + 128)));
    const height = Math.min(area.height, Math.max(400, Math.min(780, dimensions.height / display.scaleFactor + 96)));
    const bounds = { x: Math.round(area.x + (area.width - width) / 2), y: Math.round(area.y + (area.height - height) / 2), width: Math.round(width), height: Math.round(height) };
    resultEditor = true;
    resultReady = false;
    closePanel();
    // Only the live mask must be excluded from native capture. Once sampling
    // stops, the result is an ordinary window the user can screenshot again.
    win!.setContentProtection(false);
    win!.setIgnoreMouseEvents(false);
    win!.setAlwaysOnTop(false);
    win!.setSkipTaskbar(false);
    win!.setFocusable(true);
    win!.setMovable(true);
    win!.setResizable(true);
    win!.setMinimumSize(Math.min(420, area.width), Math.min(300, area.height));
    win!.setBounds(bounds);
    win!.setTitle("Nook · 长截图");
    win!.show();
    win!.focus();
    // Once sampling ends, Esc belongs to this normal editor, not other apps.
    suspendCaptureEscape();
    event.returnValue = bounds;
});

ipcMain.on("ipc", (event, key, args) => {
    if (!trusted(event) || terminal) { event.returnValue = null; return; }
    // Electron's returnValue setter sends the synchronous IPC reply at once.
    // Do not send a placeholder null before computing the actual query value:
    // a second assignment cannot replace the first reply received upstream.
    let reply: unknown = null;
    switch (key) {
        case "clip_show": if (captureStarted && !cancelAfterExport) { if (resultEditor) win?.show(); else if (!resultReady) showInactiveIfHidden(win); } break;
        case "clip_close":
        case "windowClose": finish("cancelled"); break;
        case "windowIgnoreMouse":
            // Legacy renderer messages cannot make the fullscreen sampling
            // layer intercept input or re-enable passthrough in the editor.
            win?.setIgnoreMouseEvents(!resultEditor);
            break;
        case "getMousePos": {
            if (!captureStarted) break;
            const point = screen.getCursorScreenPoint();
            const bounds = win!.getBounds();
            const overPanel = [panelWin, previewWin].some(surface => {
                if (!surface || surface.isDestroyed()) return false;
                const bounds = surface.getBounds();
                return point.x >= bounds.x && point.x < bounds.x + bounds.width && point.y >= bounds.y && point.y < bounds.y + bounds.height;
            });
            reply = { ab: point, po: { x: point.x - bounds.x, y: point.y - bounds.y }, overPanel };
            break;
        }
        case "systemLan": reply = "zh-HANS"; break;
        case "runPath": reply = path.resolve(__dirname, "../.."); break;
        case "getScreens": reply = captureStarted ? screen.getAllDisplays() : []; break;
        case "dialogMessage": finish("error", { message: String(args?.[0]?.message ?? "Screen capture initialization failed") }); reply = 0; break;
        case "clip_hide": break;
        default: break;
    }
    event.returnValue = reply;
});

app.on("will-quit", () => { terminal = true; clearInterval(startupPollingTimer); globalShortcut.unregisterAll(); });
app.on("before-quit", (event) => { if (!terminal) { event.preventDefault(); finish("cancelled"); } });
process.on("uncaughtException", (error) => { console.error(error); finish("error", { message: String(error) }); });
process.on("unhandledRejection", (error) => { console.error(error); finish("error", { message: String(error) }); });

function checkLifetime(probeParent = true) {
    if (terminal) return false;
    if (fs.existsSync(path.join(identity.outputDirectory, "cancel.request"))) finish("cancelled");
    if (terminal) return false;
    if (probeParent && identity.parentProcessId) {
            try { process.kill(identity.parentProcessId, 0); }
            catch (error) {
                if ((error as NodeJS.ErrnoException).code === "ESRCH") {
                    // No parent can acknowledge a pending transaction now.
                    // Keep its files recoverable and report uncertainty, not
                    // a false cancellation of an already committed output.
                    if (pendingExport) { pollExport(); if (!terminal) finish("error", { message: "截图主程序已退出，导出结果尚未确认；临时图片已保留。" }); }
                    else finish("cancelled");
                }
            }
    }
    return !terminal;
}

function resolveCaptureDisplay() {
    const native = require("node-screenshots") as typeof import("node-screenshots");
    const monitor = native.Monitor.all().find((m) => m.x() === request.monitor.x && m.y() === request.monitor.y && m.width() === request.monitor.width && m.height() === request.monitor.height);
    if (!monitor) throw new Error("The selected monitor changed. Please start a new screenshot.");
    const center = screen.screenToDipPoint({ x: request.monitor.x + Math.floor(request.monitor.width / 2), y: request.monitor.y + Math.floor(request.monitor.height / 2) });
    const display = screen.getDisplayNearestPoint(center);
    if (Math.abs(display.scaleFactor - request.monitor.scaleFactor) > 0.01 ||
        Math.abs(display.bounds.width * display.scaleFactor - request.monitor.width) > 2 ||
        Math.abs(display.bounds.height * display.scaleFactor - request.monitor.height) > 2)
        throw new Error("Monitor scaling changed. Please start a new screenshot.");
    return { monitor, display };
}

function prepareCapture(target: ReturnType<typeof resolveCaptureDisplay>) {
    if (terminal || stopping || !win || win.isDestroyed()) return;
    const { display, monitor } = target;
    const startCapture = () => {
        if (terminal || stopping || captureStarted || !win || win.isDestroyed() || !checkLifetime(false)) return;
        captureStarted = true;
        phase("captureStartMs");
        clearInterval(startupPollingTimer);
        win.webContents.send("ipc", "clip_init", [[{ ...display, id: monitor.id() }], undefined, monitor.id(), undefined]);
    };
    if (!request.deferredStart) { startCapture(); return; }
    writePrepared(request);
    phase("preparedMs");
    const pollStart = () => {
        if (terminal || stopping || captureStarted) { clearInterval(startupPollingTimer); return; }
        try {
            // Cancellation wins over a simultaneously published start.
            if (!checkLifetime(false)) return;
            if (readStartRequest(request)) startCapture();
        } catch (error) { finish("error", { message: String(error) }); }
    };
    startupPollingTimer = setInterval(pollStart, 30);
    pollStart();
}

function pollWarmActivation() {
    if (!warm || warmActivationPrepared || terminal || stopping) return;
    try {
        if (!checkLifetime(false)) return;
        if (!request) {
            const activation = readWarmActivation(warm);
            if (activation) {
                request = activation;
                phase("activatedMs");
                clearTimeout(warmTimer);
                startupTimer = setTimeout(() => finish("error", { message: "Screenshot editor initialization timed out" }), 18_000);
            }
        }
        if (!rendererLoaded || !rendererScriptReady || !win || win.isDestroyed()) return;
        if (!warmReady) {
            warmReady = true;
            writeWarmReady(warm);
        }
        if (!request) return;
        // All identities and the deferred handoff are validated before loading
        // native screen modules, enumerating monitors or registering Escape.
        warmActivationPrepared = true;
        clearInterval(warmPollingTimer);
        if (!checkLifetime()) return;
        const target = resolveCaptureDisplay();
        win.setBounds(target.display.bounds);
        restoreCaptureEscape();
        prepareCapture(target);
    } catch (error) { finish("error", { message: String(error) }); }
}

app.whenReady().then(async () => {
    if (terminal || !identity) return;
    phase("appReadyMs");
    if (!checkLifetime(Boolean(warm))) return;
    cancellationTimer = setInterval(() => { if (checkLifetime() && request) pollExport(); }, 250);
    const target = request && !warm ? resolveCaptureDisplay() : undefined;

    session.defaultSession.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
    session.defaultSession.setPermissionCheckHandler(() => false);
    session.defaultSession.webRequest.onBeforeRequest((details, callback) => callback({ cancel: !/^(file:|data:|blob:|devtools:)/i.test(details.url) }));
    win = new BrowserWindow({
        title: "Nook · 滚动截屏", ...(target?.display.bounds ?? { x: 0, y: 0, width: 1, height: 1 }),
        show: false, transparent: true, frame: false, resizable: false, movable: false, focusable: false,
        fullscreenable: false, skipTaskbar: true, alwaysOnTop: true, hasShadow: false,
        autoHideMenuBar: true,
        webPreferences: { nodeIntegration: true, contextIsolation: false, sandbox: false, backgroundThrottling: false, zoomFactor: 1 },
    });
    win.setMenu(null);
    // Keep the live dimming mask steady. The compositor excludes this whole
    // surface from native samples; the renderer need not blink it off per frame.
    win.setContentProtection(true);
    if (!win.isContentProtected()) throw new Error("当前系统无法排除截图遮罩，请重新截图。");
    win.setIgnoreMouseEvents(true);
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
    if (request && !warm) {
        phase("activatedMs");
        restoreCaptureEscape();
        startupTimer = setTimeout(() => finish("error", { message: "Screenshot editor initialization timed out" }), 18_000);
    }
    if (warm) warmPollingTimer = setInterval(pollWarmActivation, 50);
    await win.loadFile(path.join(__dirname, "../renderer/capture.html"));
    if (terminal || !win || win.isDestroyed()) return;
    rendererLoaded = true;
    phase("rendererLoadedMs");
    if (target) prepareCapture(target);
    else pollWarmActivation();
}).catch((error) => finish("error", { message: String(error) }));
