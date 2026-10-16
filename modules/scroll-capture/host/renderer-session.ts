type NookRequest = import("../../main/protocol.mjs").CaptureRequest;
type NookAction = import("../../main/protocol.mjs").CaptureAction;
const nookIPC = (require("electron") as typeof import("electron")).ipcRenderer;
classRegistry.setClass(arrow);
classRegistry.setSVGClass(arrow);
const nookRequest = nookIPC.sendSync("nook-request") as NookRequest;
let nookDisposed = false;
let nookFinishing = false;
let nookExporting = false;
let nookInitialImported = false;
let nookLimitReached = false;

async function nookCaptureFrame() {
    // The compact preview may overlap a nearly full-screen crop. Hide all of
    // our rolling overlays for two paints so no controls enter the next frame.
    const overlays = nookCaptureOverlays();
    // Opacity keeps buttons hit-testable during sampling; visibility:hidden
    // would swallow a quick click on Finish/Cancel while painting the frame.
    const opacity = overlays.map((element) => element.style.opacity);
    for (const element of overlays) element.style.opacity = "0";
    try {
        await new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
        await new Promise((resolve) => setTimeout(resolve, 16));
        if (nookDisposed) return undefined;
        const frame = (await getNowScreen().capture()).toImageData();
        if (!frame && !nookDisposed) throw new Error("无法取得屏幕内容，请重新截图。");
        return frame ?? undefined;
    } finally {
        if (!nookDisposed && longRunning)
            overlays.forEach((element, index) => { element.style.opacity = opacity[index]; });
    }
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
    if (nookDisposed || nookExporting || longRunning || nookFinishing) return;
    nookExporting = true;
    try {
        fabricCanvas.discardActiveObject();
        fabricCanvas.renderAll();
        const blob = await getBlob(getClipPhoto(), "image/png");
        if (!blob) throw new Error("The edited image could not be exported");
        if (nookDisposed) return;
        nookIPC.send("nook-complete", action, Buffer.from(await blob.arrayBuffer()));
    } catch (error) { nookFail(error); }
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
            case "text": object = new IText(mark.text ?? "", { left: mark.x1 + x, top: mark.y1 + y, fontSize: mark.fontSize, fontFamily: "Segoe UI, Microsoft YaHei, sans-serif", fill: color, padding: 3 }); object.形状 = "text"; break;
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
    const zoom = Math.min(1 / ratio, Math.max(0.03, (window.innerWidth - 140) / mainCanvas.width));
    setEditorP(zoom, 72 / zoom, 16 / zoom);
    drawClipRect();
    followBar();
    if (nookLimitReached) alert("已达到长截图大小上限，已保留目前内容；可以编辑或保存。");
}

toolsX.copy.f = () => { void nookComplete("copy"); };
toolsX.save.f = () => { void nookComplete("save"); };
toolsX.ding.f = () => { void nookComplete("pin"); };
const nookCollectButton = iconEl("copy").attr({ id: "nook_collect", title: "收藏到 Nook 素材库" }).on("click", () => { void nookComplete("collect"); });
nookCollectButton.el.innerHTML = '<svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M5 4h14v17l-7-4-7 4z"/><path d="M9 9h6M12 6v6"/></svg>';
nookCollectButton.style({ color: "#8b5cf6" });
toolBarEl.add(nookCollectButton);
