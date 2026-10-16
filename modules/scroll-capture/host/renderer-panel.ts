// A persistent shell around the upstream live image. The image slot can be
// replaced by longPutImg without removing the controls or their hit targets.
type NookPanelBounds = { x: number; y: number; width: number; height: number };
let nookPanelBounds: NookPanelBounds | undefined;
let nookPanelDrag: { x: number; y: number; left: number; top: number } | undefined;
let nookPointerIgnored: boolean | undefined;
const nookPanel = document.createElement("section");
nookPanel.id = "nook_scroll_panel";
nookPanel.setAttribute("aria-label", "滚动截屏实时预览");
nookPanel.hidden = true;
const nookPanelHeader = document.createElement("header");
nookPanelHeader.textContent = "滚动截屏";
nookPanelHeader.title = "拖动此处可移动预览面板";
const nookPanelHint = document.createElement("p");
nookPanelHint.textContent = "在选区内滚动原页面";
const nookPanelStatus = document.createElement("div");
nookPanelStatus.id = "nook_scroll_status";
nookPanelStatus.setAttribute("role", "status");
nookPanelStatus.setAttribute("aria-live", "polite");
const nookPanelActions = document.createElement("footer");
const nookPanelCancel = document.createElement("button");
nookPanelCancel.type = "button";
nookPanelCancel.textContent = "取消";
nookPanelCancel.title = "取消整次截图（Esc）";
const nookPanelEscape = document.createElement("span");
nookPanelEscape.textContent = "Esc 退出";
longPreview.el.id = "nook_scroll_preview";
longPreview.el.style.position = "relative";
longPreview.el.setAttribute("aria-label", "已拼接的长图");
finishLongB.textContent = "完成";
finishLongB.setAttribute("type", "button");
finishLongB.setAttribute("title", "完成拼接，继续编辑图片");
nookPanelActions.append(nookPanelCancel, finishLongB);
nookPanel.append(nookPanelHeader, nookPanelHint, longPreview.el, nookPanelStatus, nookPanelActions, nookPanelEscape);
document.body.append(nookPanel);

function nookLayoutPanel(rect: number[], scale: number, viewportWidth: number, viewportHeight: number): NookPanelBounds {
    const margin = 12;
    // Leave the entire shadow outside the source crop, not just the border.
    const gap = 36;
    const x = rect[0] / scale;
    const y = rect[1] / scale;
    const right = (rect[0] + rect[2]) / scale;
    const bottom = (rect[1] + rect[3]) / scale;
    const leftSpace = x - gap - margin;
    const rightSpace = viewportWidth - right - gap - margin;
    const sideSpace = Math.max(leftSpace, rightSpace);
    const width = Math.min(248, Math.max(0, viewportWidth - margin * 2), sideSpace >= 212 ? sideSpace : 248);
    const height = Math.min(460, Math.max(0, viewportHeight - margin * 2));
    let left = rightSpace >= leftSpace ? right + gap : x - width - gap;
    let top = y;
    if (sideSpace < width) {
        // A full-width crop has no outside side space. Prefer above/below,
        // otherwise float inside; capture hides overlapping UI for sampling.
        left = viewportWidth - margin - width;
        if (viewportHeight - bottom - gap - margin >= height) top = bottom + gap;
        else if (y - gap - margin >= height) top = y - gap - height;
        else top = margin;
    }
    return {
        x: Math.max(margin, Math.min(left, viewportWidth - margin - width)),
        y: Math.max(margin, Math.min(top, viewportHeight - margin - height)),
        width, height,
    };
}

function nookApplyPanelBounds() {
    if (!nookPanelBounds) return;
    Object.assign(nookPanel.style, {
        left: `${nookPanelBounds.x}px`, top: `${nookPanelBounds.y}px`,
        width: `${nookPanelBounds.width}px`, height: `${nookPanelBounds.height}px`,
    });
}

function nookIsPanelPoint(x: number, y: number) {
    const b = nookPanelBounds;
    return Boolean(b && !nookPanel.hidden && x >= b.x && x < b.x + b.width && y >= b.y && y < b.y + b.height);
}

function nookSetPointerPassthrough(ignore: boolean) {
    if (ignore === nookPointerIgnored) return;
    nookPointerIgnored = ignore;
    renderSend("windowIgnoreMouse", [ignore]);
}

function nookUpdatePointer() {
    if (nookDisposed || !longRunning) return;
    const mouse = renderSendSync("getMousePos", []);
    if (nookDisposed || !mouse?.po) return;
    // Bounds remain available even while the overlay is hidden for a frame.
    // DOM hit testing would accidentally pass a click to the source page then.
    nookSetPointerPassthrough(!nookPanelDrag && !nookIsPanelPoint(mouse.po.x, mouse.po.y));
}

function nookCanCaptureInput(_event?: unknown) {
    if (nookDisposed || nookFinishing || !longRunning || nookPanelDrag) return false;
    const mouse = renderSendSync("getMousePos", []);
    if (!mouse?.po || nookDisposed) return false;
    const { x, y } = mouse.po;
    return !nookIsPanelPoint(x, y) && x >= finalRect[0] / ratio && y >= finalRect[1] / ratio &&
        x < (finalRect[0] + finalRect[2]) / ratio && y < (finalRect[1] + finalRect[3]) / ratio;
}

function nookStartPanel(rect: number[]) {
    nookPanelBounds = nookLayoutPanel(rect, ratio, window.innerWidth, window.innerHeight);
    nookApplyPanelBounds();
    nookPanel.hidden = false;
    nookPanelStatus.textContent = "正在准备第一屏…";
    nookPanelHint.textContent = "在选区内滚动原页面";
    (finishLongB as HTMLButtonElement).disabled = true;
    finishLongB.onclick = () => { void stopLong(); };
    nookPanelCancel.onclick = nookCancelCapture;
    nookPointerIgnored = undefined;
    nookUpdatePointer();
}

function nookRefreshPanel(scrollToTop = false) {
    if (nookDisposed || !longX.img) return;
    nookPanelStatus.textContent = `已拼接 ${longX.img.width} × ${longX.img.height} px`;
    (finishLongB as HTMLButtonElement).disabled = nookFinishing;
    // Never shrink an entire long image to fit its height: it becomes a thin
    // unreadable strip. Width-fit plus a scrollable tail shows actual progress.
    longPreview.el.scrollTop = scrollToTop ? 0 : longPreview.el.scrollHeight;
}

function nookFinishingPanel() {
    (finishLongB as HTMLButtonElement).disabled = true;
    nookPanelStatus.textContent = "正在整理最后一屏…";
    nookPanelHint.textContent = "完成后可继续标注、复制或保存";
}

function nookHidePanel() {
    nookPanel.hidden = true;
    nookPanelDrag = undefined;
    nookPointerIgnored = undefined;
}

function nookCancelCapture() {
    if (nookDisposed) return;
    nookIPC.send("nook-cancel");
    nookDispose();
}

function nookCaptureOverlays(): HTMLElement[] {
    const elements: HTMLElement[] = [longTip.el.el];
    const b = nookPanelBounds;
    const shadow = 24;
    if (b && !nookPanel.hidden && b.x - shadow < (finalRect[0] + finalRect[2]) / ratio &&
        b.x + b.width + shadow > finalRect[0] / ratio && b.y - shadow < (finalRect[1] + finalRect[3]) / ratio &&
        b.y + b.height + shadow > finalRect[1] / ratio) elements.push(nookPanel);
    return elements;
}

nookPanelHeader.onpointerdown = (event) => {
    if (event.button !== 0 || !nookPanelBounds || nookDisposed) return;
    nookPanelDrag = { x: event.clientX, y: event.clientY, left: nookPanelBounds.x, top: nookPanelBounds.y };
    nookPanelHeader.setPointerCapture(event.pointerId);
    nookSetPointerPassthrough(false);
};
nookPanelHeader.onpointermove = (event) => {
    if (!nookPanelDrag || !nookPanelBounds) return;
    nookPanelBounds.x = Math.max(12, Math.min(nookPanelDrag.left + event.clientX - nookPanelDrag.x, window.innerWidth - nookPanelBounds.width - 12));
    nookPanelBounds.y = Math.max(12, Math.min(nookPanelDrag.top + event.clientY - nookPanelDrag.y, window.innerHeight - nookPanelBounds.height - 12));
    nookApplyPanelBounds();
};
nookPanelHeader.onpointerup = nookPanelHeader.onpointercancel = () => { nookPanelDrag = undefined; nookUpdatePointer(); };
nookPanelHeader.onlostpointercapture = () => { nookPanelDrag = undefined; nookUpdatePointer(); };
// Panel interactions must not reach the screenshot selection/pen handlers.
for (const name of ["pointerdown", "pointerup", "mousedown", "mouseup", "click", "dblclick", "wheel"])
    nookPanel.addEventListener(name, (event) => event.stopPropagation());
window.addEventListener("mousemove", (event) => {
    if (!nookDisposed && longRunning)
        nookSetPointerPassthrough(!nookPanelDrag && !nookIsPanelPoint(event.clientX, event.clientY));
});
