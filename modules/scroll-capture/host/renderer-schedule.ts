// Appended to the upstream renderer by prepare.mjs. This module does not install
// native hooks: its only input is an already-filterable wheel/keyup notification.
const nookFrameThrottleMs = 220;
const nookFrameSettleMs = 160;
const nookFrameBusyRetryMs = 40;
const nookFrameMinimumTailGapMs = 80;
let nookScheduledFrameTimer: ReturnType<typeof setTimeout> | undefined;
let nookScheduledFrameGeneration = 0;
let nookScheduledFrameRunning: object | null = null;
let nookScheduledFramePending = false;
let nookScheduledTailPending = false;
let nookScheduledTailAt = Number.POSITIVE_INFINITY;
let nookLastScheduledFrameAt = Number.NEGATIVE_INFINITY;

function nookScheduledCaptureActive() {
    return !nookDisposed && !nookFinishing && longRunning;
}

function nookClearScheduledFrame() {
    nookScheduledFrameGeneration++;
    if (nookScheduledFrameTimer !== undefined) clearTimeout(nookScheduledFrameTimer);
    nookScheduledFrameTimer = undefined;
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
    nookScheduledFramePending = true;
    nookScheduledTailPending = true;
    nookScheduledTailAt = Date.now() + nookFrameSettleMs;
    nookPlanScheduledFrame();
}
