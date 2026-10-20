type NookRequest = import("../../main/protocol.mjs").CaptureRequest;
type NookAction = import("../../main/protocol.mjs").CaptureAction;
const nookIPC = (require("electron") as typeof import("electron")).ipcRenderer;
classRegistry.setClass(arrow);
classRegistry.setSVGClass(arrow);
let nookRequest: NookRequest;
// var is intentionally hoisted: upstream initializes its selection tool before
// this adapter runs. Once initialized, all button and keyboard routes share it.
var nookFeaturePolicy: NookRequest["features"];
function nookBindRequest() {
    if (nookDisposed || nookRequest) return false;
    const request = nookIPC.sendSync("nook-request") as NookRequest | null;
    if (!request) throw new Error("Screenshot session is unavailable");
    nookRequest = request;
    nookFeaturePolicy = request.features;
    if (!nookAllowsFeature("pin")) toolsX.ding.el.el.remove();
    if (nookAllowsFeature("collect")) {
        const button = iconEl("star").attr({ id: "nook_collect", title: "收藏到 Nook 素材库" }).on("click", () => { void nookComplete("collect"); });
        button.style({ color: "#8b5cf6" });
        toolBarEl.add(button);
    }
    nookApplyFeatureTools();
    return true;
}
function nookAllowsFeature(name: keyof NonNullable<NookRequest["features"]>) {
    return nookFeaturePolicy?.[name] !== false;
}
function nookAllowsEditTool(mainType: string, type: string) {
    if (mainType === "select") return true;
    if (mainType === "draw") return nookAllowsFeature("pen");
    if (mainType === "filter") return nookAllowsFeature("mosaic");
    if (mainType !== "shape") return false;
    if (type === "rect" || type === "mask") return nookAllowsFeature("rectangle");
    if (type === "circle") return nookAllowsFeature("ellipse");
    if (["line", "arrow", "polyline", "polygon"].includes(type)) return nookAllowsFeature("lines");
    if (type === "text" || type === "number") return nookAllowsFeature("text");
    return false;
}
function nookHasAnnotations() {
    return (["rectangle", "ellipse", "lines", "pen", "text", "mosaic"] as const).some(nookAllowsFeature);
}
function nookAllowsOutput(action: NookAction) {
    return action === "save" || action === "copy" ||
        (action === "pin" && nookAllowsFeature("pin")) || (action === "collect" && nookAllowsFeature("collect"));
}
function nookApplyFeatureTools() {
    // Normalize the remembered subtype as well as hiding entries: a category
    // whose default is disabled should still open its first enabled tool.
    for (const [mainType, entries] of Object.entries(drawSideEls)) {
        let firstAllowed: string | undefined;
        let firstIcon = "";
        for (const [type, control] of Object.entries(entries)) {
            const allowed = nookAllowsEditTool(mainType, type);
            control.el.hidden = !allowed;
            if (!allowed) control.el.style.setProperty("display", "none", "important");
            else if (firstAllowed === undefined) { firstAllowed = type; firstIcon = control.el.innerHTML; }
        }
        const category = mainType as keyof EditType;
        const main = drawMainEls[category].el;
        if (firstAllowed === undefined) main.style.setProperty("display", "none", "important");
        else if (!nookAllowsEditTool(mainType, editType[category])) {
            (editType as Record<string, string>)[mainType] = firstAllowed;
            main.innerHTML = firstIcon;
        }
    }
}
let nookDisposed = false;
let nookFinishing = false;
let nookExporting = false;
let nookInitialImported = false;
let nookLimitReached = false;
let nookResultMode = false;
let nookResultReady = false;
type NookFinishAction = "edit" | "save" | "copy";
let nookFinishAction: NookFinishAction = "copy";
let nookResultStatus: HTMLSpanElement | undefined;
let nookResultScroll: HTMLDivElement | undefined;
let nookResultScrollContent: HTMLDivElement | undefined;

function nookWaitForCapturePaint() {
    // Chromium can stop animation callbacks for a fully transparent/occluded
    // sampling window even with backgroundThrottling:false. Painting is only a
    // short overlay-settle hint, never a condition for another native sample.
    return new Promise<void>((resolve) => {
        let done = false;
        let timer: ReturnType<typeof setTimeout> | undefined;
        let paint: number | undefined;
        const finish = () => {
            if (done) return;
            done = true;
            if (timer !== undefined) clearTimeout(timer);
            if (paint !== undefined) cancelAnimationFrame(paint);
            resolve();
        };
        timer = setTimeout(finish, 80);
        paint = requestAnimationFrame(() => {
            if (done) return;
            paint = requestAnimationFrame(finish);
        });
    });
}

async function nookCaptureFrame() {
    // The host excludes the sampler, dimmer and controls at the compositor.
    // Never fade the full-screen dimmer for a sample: even the idle fallback
    // would flash the whole desktop twice a second. No per-frame paint delay
    // is needed when these surfaces stay visible and capture-excluded.
    if (nookDisposed) return undefined;
    const image = await getNowScreen().capture();
    // A native capture already in flight cannot be aborted, but Escape must
    // discard it before pixel conversion, hashing, matching or preview updates.
    if (nookDisposed) return undefined;
    const frame = image.toImageData();
    if (!frame) throw new Error("无法取得屏幕内容，请重新截图。");
    return frame;
}

function nookDispose() {
    if (nookDisposed) return;
    nookDisposed = true;
    nookClearScheduledFrame();
    nookHidePanel();
    longRunning = false;
    nookFinishing = true;
    if (uIOhook) {
        uIOhook.removeAllListeners();
        uIOhook.stop();
        uIOhook = null;
    }
    clearInterval(longClipTime);
    if (longMouse !== null) clearInterval(longMouse);
    longMouse = null;
    renderSend("windowIgnoreMouse", [false]);
}

function nookFail(error: unknown) {
    if (nookDisposed) return;
    nookIPC.send("nook-failure", error instanceof Error ? error.message : String(error));
    nookDispose();
}

nookIPC.on("nook-dispose", () => {
    nookDispose();
    nookIPC.send("nook-disposed");
});
window.addEventListener("error", (event) => nookFail(event.error ?? event.message));
window.addEventListener("unhandledrejection", (event) => { event.preventDefault(); nookFail(event.reason); });
function nookCaptureKeydown(event: KeyboardEvent) {
    if (event.key === "Escape") {
        event.preventDefault();
        event.stopImmediatePropagation();
        nookCancelCapture();
        return;
    }
    if (nookExporting) {
        event.preventDefault();
        event.stopImmediatePropagation();
        return;
    }
    if (longRunning || nookFinishing) {
        // The upstream editor has single-letter/select-all/undo hotkeys. A
        // click on our preview must not let them change the fixed live crop.
        // Keep native button activation and Tab navigation, not editor tools.
        event.stopImmediatePropagation();
        const button = event.target === finishLongB || event.target === nookPanelCancel;
        if (event.key !== "Tab" && !(button && (event.key === "Enter" || event.key === " ")))
            event.preventDefault();
    }
}
window.addEventListener("keydown", nookCaptureKeydown, true);
window.addEventListener("keyup", (event) => {
    if (longRunning || nookFinishing) event.stopImmediatePropagation();
}, true);

async function nookInitialize() {
    if (!nookRequest || nookDisposed) throw new Error("Screenshot session is unavailable");
    const { region, monitor } = nookRequest;
    if (mainCanvas.width !== monitor.width || mainCanvas.height !== monitor.height)
        throw new Error("Screenshot dimensions do not match the selected monitor");
    ratio = monitor.scaleFactor;
    finalRect = [region.x - monitor.x, region.y - monitor.y, region.width, region.height];
    finalRectFix();
    setEditorP(1 / ratio, 0, 0);
    drawClipRect();
    setEditType("select", "rect");
    await startLong();
    if (!nookDisposed) nookIPC.send("nook-initialized");
}

async function nookComplete(action: NookAction) {
    if (!nookRequest || nookDisposed || nookExporting || longRunning || nookFinishing || !nookAllowsOutput(action)) return;
    nookExporting = true;
    nookSetExportStatus(action === "save" ? "选择保存位置…" : "正在处理…", true);
    try {
        fabricCanvas.discardActiveObject();
        fabricCanvas.renderAll();
        const blob = await getBlob(getClipPhoto(), "image/png");
        if (!blob) throw new Error("The edited image could not be exported");
        if (nookDisposed) return;
        const bytes = Buffer.from(await blob.arrayBuffer());
        if (nookDisposed) return;
        nookIPC.send("nook-complete", action, bytes);
    } catch (error) {
        nookExporting = false;
        nookSetExportStatus(`未完成：${error instanceof Error ? error.message : String(error)}；图片仍可编辑。`, false);
    }
}

function nookSetExportStatus(message: string, busy: boolean) {
    document.body.classList.toggle("nook-exporting", busy);
    if (nookResultStatus) {
        nookResultStatus.textContent = message;
        nookResultStatus.title = message;
    }
    if (!nookResultMode && nookResultReady)
        nookSendPanel(message, !busy, undefined, false, message, !busy);
}
function nookHandleExportReply(status: unknown, message: unknown) {
    if (nookDisposed) return;
    nookExporting = false;
    nookSetExportStatus(status === "cancelled" ? "已取消保存，图片已保留；可重试、编辑或复制。" : `未完成：${String(message || "请重试或换一种输出方式。")}`, false);
}
nookIPC.on("nook-export-reply", (_event, status, message) => nookHandleExportReply(status, message));

async function nookRequestFinish(action: unknown = "copy") {
    if (nookDisposed || nookFinishing || nookExporting || nookResultMode) return;
    if (action !== "copy" && action !== "save" && action !== "edit") return;
    nookFinishAction = action;
    if (longRunning) await stopLong();
    else if (nookResultReady) await nookRunResultAction(action);
}

function nookPrepareResult() {
    if (nookDisposed || nookResultReady) return;
    // pjLong has finished importing all initial editable annotations before
    // this transaction freezes the source window. No output can race that work.
    const ready = nookIPC.sendSync("nook-result-ready", { width: mainCanvas.width, height: mainCanvas.height });
    if (!ready) throw new Error("无法保留长截图结果，请重新截图。");
    nookResultReady = true;
    // The retained thumbnail must agree with the output when the user had
    // already marked the original screenshot before starting a long capture.
    fabricCanvas.renderAll();
    const preview = nookRequest.initialAnnotations.length ? nookPanelPreview(false, getClipPhoto()) : nookPanelPreview();
    const status = nookLimitReached ? "已达到大小上限，已保留当前内容；可编辑、保存或复制" : "长截图已保留";
    nookSendPanel(status, true, preview, false, nookLimitReached ? status : "编辑、保存或完成复制", true);
}

async function nookRunResultAction(action: NookFinishAction) {
    if (nookDisposed || nookFinishing || nookExporting || !nookResultReady || nookResultMode) return;
    if (action === "edit") {
        try { await nookEnterResultEditor(); }
        catch (error) { nookSetExportStatus(`未完成：${error instanceof Error ? error.message : String(error)}`, false); }
    } else await nookComplete(action);
}

function nookResultTool(icon: IconType, title: string, action: () => void) {
    const button = document.createElement("button");
    button.type = "button";
    button.title = title;
    button.setAttribute("aria-label", title);
    button.append(iconEl(icon).el);
    button.onclick = action;
    return button;
}

function nookConstrainResultPosition(zoom: number, x: number, y: number) {
    const imageWidth = mainCanvas.width * zoom;
    const imageHeight = mainCanvas.height * zoom;
    const viewportWidth = Math.max(1, window.innerWidth - 16);
    const left = imageWidth <= viewportWidth - 48 ? (viewportWidth - imageWidth) / 2 :
        Math.max(viewportWidth - 24 - imageWidth, Math.min(24, x * zoom));
    const top = Math.max(Math.min(56, window.innerHeight - 24 - imageHeight), Math.min(56, y * zoom));
    if (nookResultScroll && nookResultScrollContent) {
        nookResultScrollContent.style.height = `${Math.max(window.innerHeight - 44, imageHeight + 36)}px`;
        const scrollTop = Math.max(0, 56 - top);
        if (Math.abs(nookResultScroll.scrollTop - scrollTop) > .5) nookResultScroll.scrollTop = scrollTop;
    }
    return { x: left / zoom, y: top / zoom };
}

async function nookEnterResultEditor() {
    if (nookDisposed || nookResultMode) return;
    const bounds = nookIPC.sendSync("nook-result-editor", { width: mainCanvas.width, height: mainCanvas.height });
    if (!bounds) throw new Error("无法打开长截图编辑窗口，图片已保留。");
    nookResultMode = true;
    nookHidePanel();
    for (const element of longHide) element.style.display = "";
    document.body.classList.add("nook-result-editor");
    const header = document.createElement("header");
    header.id = "nook_result_header";
    const tools = document.createElement("nav");
    tools.id = "nook_result_tools";
    tools.setAttribute("aria-label", "图片标注工具");
    if (nookHasAnnotations()) tools.append(nookResultTool("draw_select", "选择并移动标注", () => setEditType("select", "draw")));
    if (nookAllowsFeature("rectangle")) tools.append(nookResultTool("rect", "矩形", () => setEditType("shape", "rect")));
    if (nookAllowsFeature("ellipse")) tools.append(nookResultTool("circle", "椭圆", () => setEditType("shape", "circle")));
    if (nookAllowsFeature("lines")) tools.append(nookResultTool("arrow", "箭头", () => setEditType("shape", "arrow")));
    if (nookAllowsFeature("pen")) tools.append(nookResultTool("draw", "画笔", () => setEditType("draw", "free")));
    if (nookAllowsFeature("mosaic")) tools.append(nookResultTool("pixelate", "马赛克", () => setEditType("filter", "pixelate")));
    if (nookAllowsFeature("text")) tools.append(nookResultTool("text", "文字", () => setEditType("shape", "text")));
    if (nookHasAnnotations()) tools.append(
        nookResultTool("last", "撤销（Ctrl + Z）", () => undo(true)),
        nookResultTool("next", "重做（Ctrl + Y）", () => undo(false)),
        nookResultTool("setting", "颜色、粗细与更多工具", () => {
            const expanded = document.body.classList.toggle("nook-result-options");
            if (expanded) showSideBarItem(4);
        }),
    );
    nookResultStatus = document.createElement("span");
    nookResultStatus.id = "nook_result_status";
    nookResultStatus.setAttribute("role", "status");
    nookResultStatus.setAttribute("aria-live", "polite");
    nookResultStatus.textContent = "滚轮浏览 · Ctrl + 滚轮缩放";
    const close = document.createElement("button");
    close.type = "button";
    close.title = "退出截图（Esc）";
    close.setAttribute("aria-label", "退出截图");
    close.append(iconEl("close").el);
    close.onclick = nookCancelCapture;
    header.append(tools, toolBarEl.el, close);
    document.body.append(header);
    document.body.append(nookResultStatus);
    nookResultScroll = document.createElement("div");
    nookResultScroll.id = "nook_result_scroll";
    nookResultScroll.setAttribute("aria-label", "浏览长截图");
    nookResultScrollContent = document.createElement("div");
    nookResultScroll.append(nookResultScrollContent);
    nookResultScroll.addEventListener("scroll", () => {
        if (!nookDisposed && nookResultMode && !nookExporting)
            setEditorP(editorP.zoom, editorP.x, (56 - nookResultScroll!.scrollTop) / editorP.zoom);
    });
    document.body.append(nookResultScroll);
    window.addEventListener("resize", () => {
        if (!nookDisposed && nookResultMode && !nookExporting)
            setEditorP(editorP.zoom, editorP.x, editorP.y);
    });
    for (const name of ["pointerdown", "mousedown", "mouseup", "click", "dblclick", "wheel"])
        header.addEventListener(name, (event) => event.stopPropagation());
    // Paint can be suspended while the source renderer is hidden. Use the
    // same bounded settle as capture rather than awaiting rAF indefinitely.
    await nookWaitForCapturePaint();
    if (nookDisposed) return;
    const zoom = Math.min(1 / ratio, Math.max(0.03, (window.innerWidth - 48) / mainCanvas.width));
    setEditorP(zoom, Math.max(24, (window.innerWidth - mainCanvas.width * zoom) / 2) / zoom, 56 / zoom);
    zoomW = mainCanvas.width * zoom;
    drawClipRect();
    followBar();
    if (nookLimitReached) nookSetExportStatus("已达到大小上限，当前内容已保留；可继续编辑、保存或复制。", false);
}

function nookColor(value: string) {
    if (value.length === 9) {
        const alpha = Number.parseInt(value.slice(1, 3), 16) / 255;
        return `rgba(${Number.parseInt(value.slice(3, 5), 16)},${Number.parseInt(value.slice(5, 7), 16)},${Number.parseInt(value.slice(7, 9), 16)},${alpha})`;
    }
    return value;
}

async function nookImportInitialAnnotations() {
    if (nookInitialImported || nookDisposed) return;
    nookInitialImported = true;
    const x = -longX.imgXY.x;
    const y = -longX.imgXY.y;
    const clip = nookRequest.annotationsClip;
    const objects: FabricObject[] = [];
    for (const mark of nookRequest.initialAnnotations) {
        const left = Math.min(mark.x1, mark.x2) + x;
        const top = Math.min(mark.y1, mark.y2) + y;
        const width = Math.max(0.1, Math.abs(mark.x2 - mark.x1));
        const height = Math.max(0.1, Math.abs(mark.y2 - mark.y1));
        const color = nookColor(mark.color);
        const props = { stroke: color, strokeWidth: mark.strokeWidth, fill: "transparent", strokeLineCap: "round" as const, strokeLineJoin: "round" as const };
        let object: FabricObject;
        switch (mark.kind) {
            case "rect": object = new Rect({ left, top, width, height, ...props }); object.形状 = "rect"; break;
            case "ellipse": object = new Ellipse({ left, top, rx: width / 2, ry: height / 2, ...props }); object.形状 = "circle"; break;
            case "line": object = new Line([mark.x1 + x, mark.y1 + y, mark.x2 + x, mark.y2 + y], props); object.形状 = "line"; break;
            case "arrow": object = new arrow([mark.x1 + x, mark.y1 + y, mark.x2 + x, mark.y2 + y], props); object.形状 = "arrow"; break;
            case "text": {
                const props = { left: mark.x1 + x, top: mark.y1 + y, fontSize: mark.fontSize, fontFamily: "Segoe UI, Microsoft YaHei, sans-serif", fill: color, padding: 3 };
                object = mark.textWrapWidth ? new Textbox(mark.text ?? "", { ...props, width: mark.textWrapWidth, splitByGrapheme: true }) : new IText(mark.text ?? "", props);
                object.形状 = "text"; break;
            }
            case "pen": {
                const points = mark.points!.map((p) => ({ x: p.x + x, y: p.y + y }));
                object = points.length === 1 ? new Circle({ left: points[0].x - mark.strokeWidth / 2, top: points[0].y - mark.strokeWidth / 2, radius: mark.strokeWidth / 2, fill: color, strokeWidth: 0 }) : new Polyline(points, props);
                break;
            }
            case "mosaic": {
                const fs = require("node:fs") as typeof import("node:fs");
                const stat = fs.lstatSync(mark.mosaicPath!);
                if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 64 * 1024 * 1024) throw new Error("Invalid mosaic annotation image");
                const data = fs.readFileSync(mark.mosaicPath!);
                object = await FabricImage.fromURL(`data:image/png;base64,${data.toString("base64")}`);
                object.set({ left, top, scaleX: width / object.width, scaleY: height / object.height });
                break;
            }
        }
        object.clipPath = new Rect({ left: x + clip.x, top: y + clip.y, width: clip.width, height: clip.height, absolutePositioned: true });
        object.canChangeFill = mark.kind === "text";
        objects.push(object);
    }
    if (nookDisposed) return;
    if (objects.length) fabricCanvas.add(...objects);
    fabricCanvas.requestRenderAll();
    hisPush();
    setEditType("select", "draw");
    // This host owns one live-capture session. A fresh rolling capture must be
    // started from Nook, not against the stitched editor bitmap.
    toolsX.long.el.el.style.display = "none";
}

toolsX.copy.f = () => { void nookComplete("copy"); };
toolsX.save.f = () => { void nookComplete("save"); };
toolsX.ding.f = () => { void nookComplete("pin"); };
// A pending output must not export stale pixels while the user edits underneath.
for (const name of ["pointerdown", "mousedown", "wheel"])
    window.addEventListener(name, (event) => {
        if (nookExporting && !(event.target as HTMLElement)?.closest("#nook_result_header button")) {
            event.preventDefault(); event.stopImmediatePropagation();
        }
    }, { capture: true, passive: false });
// loadFile alone also resolves for a page whose script failed. The host only
// publishes warm readiness after this final adapter statement has executed.
nookIPC.send("nook-renderer-loaded");
