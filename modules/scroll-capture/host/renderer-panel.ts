// The capture renderer owns pixels only. Its fullscreen window always passes
// input through; an independent native panel owns live preview and actions.
type NookPanelBounds = { x: number; y: number; width: number; height: number };
let nookPanelOpen = false;
let nookPanelListening = false;
// Retained for the upstream serial capture guard. Native dragging no longer
// depends on DOM pointer capture; getMousePos.overPanel uses live OS bounds.
let nookPanelDrag: undefined;
// The inherited keyboard guard compares against this detached button. Native
// panel controls have their own keyboard handling and never enter that editor.
const nookPanelCancel = document.createElement("button");
longPreview.el.hidden = true;
longPreview.el.style.display = "none";

function nookPanelSelection(rect: number[], scale: number): NookPanelBounds {
    // The host lays out the thumbnail and toolbar separately using the live
    // monitor work area. Supply only a monitor-relative DIP crop, never a
    // second renderer-computed panel rectangle or global monitor origin.
    return { x: rect[0] / scale, y: rect[1] / scale, width: rect[2] / scale, height: rect[3] / scale };
}

function nookUpdatePointer() {
    // No mouse polling, forwarded movement, or asynchronous false/true toggle
    // participates in hit testing. This call remains an upstream adapter hook.
}

function nookCanCaptureInput(_event?: unknown) {
    if (nookDisposed || nookFinishing || !longRunning) return false;
    const mouse = renderSendSync("getMousePos", []) as { po?: { x: number; y: number }; overPanel?: boolean } | undefined;
    if (!mouse?.po || nookDisposed || mouse.overPanel) return false;
    const { x, y } = mouse.po;
    return x >= finalRect[0] / ratio && y >= finalRect[1] / ratio &&
        x < (finalRect[0] + finalRect[2]) / ratio && y < (finalRect[1] + finalRect[3]) / ratio;
}

function nookStartPanel(rect: number[]) {
    if (nookDisposed || nookPanelOpen) return;
    if (!nookIPC.sendSync("nook-panel-open", { selection: nookPanelSelection(rect, ratio) }))
        throw new Error("无法打开滚动截屏控制面板，请重新截图。");
    nookPanelOpen = true;
    if (!nookPanelListening) {
        nookPanelListening = true;
        nookIPC.on("nook-panel-finish", (_event, action: unknown) => {
            if (nookPanelOpen) void nookRequestFinish(action);
        });
    }
    nookSendPanel("准备第一屏…", false);
}

function nookSendPanel(status: string, finishEnabled: boolean, preview?: string, scrollToTop = false, title = "在原页面滚动；Esc 取消整次截图", resume = false) {
    if (nookDisposed || !nookPanelOpen) return;
    // The host validates these UTF-16 lengths before accepting resume. Native
    // export errors may be longer; their text must not discard retry controls.
    const boundedText = (text: string, limit: number) => text.length <= limit ? text :
        `${text.slice(0, limit - 1).replace(/[\uD800-\uDBFF]$/, "")}…`;
    nookIPC.send("nook-panel-update", { status: boundedText(status, 240), title: boundedText(title, 500), finishEnabled, preview, scrollToTop, resume,
        imageWidth: longX.img?.width, imageHeight: longX.img?.height });
}

function nookPanelPreview(_scrollToTop = false, source = longX.img) {
    if (!source || source.width < 1 || source.height < 1) return undefined;
    // Whole-image progress, not a moving tail. The host fits this bounded
    // thumbnail to its bottom-aligned preview; original pixels stay intact.
    const scale = Math.min(1, 480 / source.width, 2048 / source.height);
    const preview = document.createElement("canvas");
    preview.width = Math.max(1, Math.round(source.width * scale));
    preview.height = Math.max(1, Math.round(source.height * scale));
    const context = preview.getContext("2d");
    if (!context) return undefined;
    context.drawImage(source, 0, 0, source.width, source.height, 0, 0, preview.width, preview.height);
    return preview.toDataURL("image/png");
}

function nookRefreshPanel(scrollToTop = false) {
    if (nookDisposed || !nookPanelOpen || !longX.img) return;
    // Only called after a successful serial stitch, never by pointer polling.
    nookSendPanel(nookFinishing ? "正在确认最后一屏…" : `${longX.img.width} × ${longX.img.height} px`,
        !nookFinishing, nookPanelPreview(scrollToTop), scrollToTop,
        "已截取的完整内容；完成直接复制，编辑可继续标注。Esc 取消整次截图");
}

function nookFinishingPanel() { nookSendPanel("正在确认最后一屏…", false); }
function nookResumePanel() {
    if (nookMatchUnresolved) { nookMatchProblem("未找到连续内容，请向回慢慢滚动后重试", true); return; }
    nookSendPanel("页面仍在变化，请停止滚动后重试", Boolean(longX.img), undefined, false,
        "已保留当前长图。请停止滚动、暂停动画后再点完成；Esc 取消整次截图", true);
}
function nookMatchProblem(message: string, resume = false) {
    nookSendPanel(message, Boolean(longX.img), undefined, false,
        "已保留已拼接内容。请慢慢向回滚动，找到与上一屏重叠的位置；Esc 取消整次截图", resume);
}
function nookHidePanel() {
    if (!nookPanelOpen) return;
    nookPanelOpen = false;
    nookIPC.send("nook-panel-close");
}
function nookCancelCapture() {
    if (nookDisposed) return;
    nookIPC.send("nook-cancel");
    nookDispose();
}
