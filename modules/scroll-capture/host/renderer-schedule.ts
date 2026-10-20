// Appended to the upstream renderer by prepare.mjs. This module does not install
// native hooks. Events and a bounded fallback share one serial capture queue.
const nookFrameThrottleMs = 220;
const nookFrameSettleMs = 160;
const nookFrameBusyRetryMs = 40;
const nookFrameMinimumTailGapMs = 80;
const nookFrameFallbackMs = 500;
const nookFinishStableMs = 400;
const nookFinishMinimumMs = 600;
const nookFinishTimeoutMs = 6000;
let nookScheduledFrameTimer: ReturnType<typeof setTimeout> | undefined;
let nookFallbackFrameTimer: ReturnType<typeof setTimeout> | undefined;
let nookFallbackFramesEnabled = false;
let nookScheduledFrameGeneration = 0;
let nookScheduledFrameRunning: object | null = null;
let nookScheduledFramePending = false;
let nookScheduledTailPending = false;
let nookScheduledTailAt = Number.POSITIVE_INFINITY;
let nookLastScheduledFrameAt = Number.NEGATIVE_INFINITY;
let nookLastCaptureInputAt = Number.NEGATIVE_INFINITY;
let nookObservedFrameAt = Number.NEGATIVE_INFINITY;
let nookChangedFrameAt = Number.NEGATIVE_INFINITY;
let nookObservedFrameHash: string | undefined;

// Hash every pixel in the actual crop, not a sparse thumbnail: a small line of
// text must count as a change. Native SHA-256 needs no retained full-screen copy
// and skips the much more expensive matcher for identical fallback frames.
function nookObserveFrame(frame: ImageData) {
    const [x, y, width, height] = finalRect;
    if (![x, y, width, height].every(Number.isInteger) || x < 0 || y < 0 || width < 1 || height < 1 ||
        x + width > frame.width || y + height > frame.height || frame.data.length !== frame.width * frame.height * 4)
        throw new Error("截图区域尺寸改变，请重新开始滚动截屏。");
    const hash = (require("node:crypto") as typeof import("node:crypto")).createHash("sha256");
    hash.update(`${x},${y},${width},${height}:`);
    if (x === 0 && width === frame.width) hash.update(frame.data.subarray(y * width * 4, (y + height) * width * 4));
    else for (let row = y; row < y + height; row++) {
        const start = (row * frame.width + x) * 4;
        hash.update(frame.data.subarray(start, start + width * 4));
    }
    const value = hash.digest("hex");
    nookObservedFrameAt = Date.now();
    if (value === nookObservedFrameHash) return false;
    nookObservedFrameHash = value;
    nookChangedFrameAt = nookObservedFrameAt;
    return true;
}

function nookScheduledCaptureActive() {
    return !nookDisposed && !nookFinishing && longRunning;
}

function nookClearScheduledFrame() {
    nookScheduledFrameGeneration++;
    if (nookScheduledFrameTimer !== undefined) clearTimeout(nookScheduledFrameTimer);
    if (nookFallbackFrameTimer !== undefined) clearTimeout(nookFallbackFrameTimer);
    nookScheduledFrameTimer = undefined;
    nookFallbackFrameTimer = undefined;
    nookFallbackFramesEnabled = false;
    nookScheduledFramePending = false;
    nookScheduledTailPending = false;
    nookScheduledTailAt = Number.POSITIVE_INFINITY;
    nookLastScheduledFrameAt = Number.NEGATIVE_INFINITY;
    // Keep the in-flight token until its promise settles. Clearing a timer must
    // not permit a second long_s call while the previous native frame is active.
}

function nookPlanScheduledFrame() {
    if (nookScheduledFrameTimer !== undefined) clearTimeout(nookScheduledFrameTimer);
    nookScheduledFrameTimer = undefined;
    if (!nookScheduledCaptureActive()) { nookClearScheduledFrame(); return; }
    if (nookScheduledFrameRunning || (!nookScheduledFramePending && !nookScheduledTailPending)) return;
    const throttleAt = nookScheduledFramePending ? nookLastScheduledFrameAt + nookFrameThrottleMs : Number.POSITIVE_INFINITY;
    const tailAt = nookScheduledTailPending ? Math.max(nookScheduledTailAt, nookLastScheduledFrameAt + nookFrameMinimumTailGapMs) : Number.POSITIVE_INFINITY;
    const delay = longBusy ? nookFrameBusyRetryMs : Math.max(0, Math.min(throttleAt, tailAt) - Date.now());
    nookScheduledFrameTimer = setTimeout(() => { void nookRunScheduledFrame(); }, delay);
}

async function nookRunScheduledFrame() {
    nookScheduledFrameTimer = undefined;
    if (!nookScheduledCaptureActive()) { nookClearScheduledFrame(); return; }
    if (nookScheduledFrameRunning || longBusy) { nookPlanScheduledFrame(); return; }
    const now = Date.now();
    const throttleReady = nookScheduledFramePending && now >= nookLastScheduledFrameAt + nookFrameThrottleMs;
    const tailReady = nookScheduledTailPending && now >= Math.max(nookScheduledTailAt, nookLastScheduledFrameAt + nookFrameMinimumTailGapMs);
    if (!throttleReady && !tailReady) { nookPlanScheduledFrame(); return; }
    nookScheduledFramePending = false;
    if (tailReady) nookScheduledTailPending = false;
    nookLastScheduledFrameAt = now;
    const generation = nookScheduledFrameGeneration;
    const token = {};
    nookScheduledFrameRunning = token;
    try {
        await long_s();
    } catch (error) {
        // long_s normally owns capture errors. Preserve cleanup if an adapter
        // replacement ever rejects, without reviving a cancelled session.
        if (generation === nookScheduledFrameGeneration && nookScheduledCaptureActive()) nookFail(error);
    } finally {
        if (nookScheduledFrameRunning === token) {
            nookScheduledFrameRunning = null;
            nookPlanScheduledFrame();
        }
    }
}

function nookScheduleFrame(event: unknown) {
    if (!nookScheduledCaptureActive()) { nookClearScheduledFrame(); return; }
    // The session supplies physical monitor/selection/panel hit testing. Do not
    // queue captures for another monitor or for the preview's own controls.
    if (!nookCanCaptureInput(event)) return;
    nookLastCaptureInputAt = Date.now();
    nookScheduledFramePending = true;
    nookScheduledTailPending = true;
    nookScheduledTailAt = Date.now() + nookFrameSettleMs;
    nookPlanScheduledFrame();
}

function nookStartFallbackFrames() {
    if (!nookScheduledCaptureActive() || nookFallbackFramesEnabled) return;
    nookFallbackFramesEnabled = true;
    function tick() {
        nookFallbackFrameTimer = undefined;
        if (!nookFallbackFramesEnabled || !nookScheduledCaptureActive()) return;
        // A scrollbar is normally outside the crop, and middle-button scrolling
        // can continue with no wheel events. Do not require a pointer hit here.
        // Busy work is never queued again, and event-driven frames take priority.
        if (!nookPanelDrag && !longBusy && !nookScheduledFrameRunning &&
            Date.now() - nookLastScheduledFrameAt >= nookFrameFallbackMs) {
            nookScheduledFramePending = true;
            nookPlanScheduledFrame();
        }
        nookFallbackFrameTimer = setTimeout(tick, nookFrameFallbackMs);
    }
    nookFallbackFrameTimer = setTimeout(tick, nookFrameFallbackMs);
}

async function nookFinishScrollFrames(): Promise<boolean> {
    const started = Date.now();
    const deadline = started + nookFinishTimeoutMs;
    // Observe pending capture completion instead of awaiting an unbounded native
    // promise. Escape/disposal and the deadline can end this wait independently.
    while (!nookDisposed && Date.now() < deadline) {
        if (longBusy || nookScheduledFrameRunning || nookPanelDrag) {
            await new Promise((resolve) => setTimeout(resolve, 25));
            continue;
        }
        if (longCapped) return true; // Preserve the explicitly marked partial image.
        let complete = false;
        let failure: unknown;
        void long_s().then(() => { complete = true; }, (error) => { failure = error; complete = true; });
        while (!complete && !nookDisposed && Date.now() < deadline)
            await new Promise((resolve) => setTimeout(resolve, 25));
        if (nookDisposed) return false;
        if (failure) throw failure;
        if (!complete || Date.now() >= deadline) return false;
        if (longCapped) return true;
        // Use observation times, not matcher completion times: a slow match is
        // not evidence that the source page has stopped moving in the meantime.
        // An unchanged rejected frame is stable, but not stitched. Never let
        // hash dedup turn a no-overlap warning into a truncated "success".
        if (!nookMatchUnresolved && nookObservedFrameAt >= started + nookFinishMinimumMs &&
            nookObservedFrameAt - nookChangedFrameAt >= nookFinishStableMs &&
            nookObservedFrameAt - nookLastCaptureInputAt >= nookFinishStableMs) return true;
        await new Promise((resolve) => setTimeout(resolve, Math.min(nookFrameSettleMs, Math.max(0, deadline - Date.now()))));
    }
    return false;
}
