import assert from "node:assert/strict";
import fs from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
let ts;
try { ts = require("typescript"); }
catch {
    const upstream = process.env.NOOK_ESEARCH_SOURCE || path.resolve(here, "../../../outputs/esearch-15.6.0-source");
    ts = require(path.join(upstream, "node_modules/typescript/lib/typescript.js"));
}
const source = fs.readFileSync(path.join(here, "../host/renderer-schedule.ts"), "utf8");
const javascript = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}

function harness(options = {}) {
    let now = 0;
    let id = 0;
    const timers = new Map();
    const calls = [];
    const errors = [];
    const context = vm.createContext({
        console, require,
        nookDisposed: false, nookFinishing: false, longRunning: true, longBusy: false,
        longCapped: false, nookMatchUnresolved: false, nookPanelDrag: undefined, finalRect: options.rect ?? [0, 0, 4, 4],
        Date: { now: () => now },
        setTimeout: (callback, delay) => {
            const timer = ++id;
            timers.set(timer, { callback, at: now + delay });
            return timer;
        },
        clearTimeout: (timer) => timers.delete(timer),
        nookCanCaptureInput: options.canCapture ?? ((event) => event?.inside === true && !event?.panel),
        nookFail: (error) => errors.push(error),
        long_s: () => {
            calls.push(now);
            return options.capture?.(context, now) ?? Promise.resolve();
        },
    });
    vm.runInContext(javascript, context);
    async function flush() { for (let i = 0; i < 8; i++) await Promise.resolve(); }
    async function advance(milliseconds) {
        const end = now + milliseconds;
        let iterations = 0;
        while (true) {
            const next = [...timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0];
            if (!next) break;
            assert.ok(++iterations < 1000, "scheduler must not create a zero-delay busy loop");
            const [timerId, timer] = next;
            timers.delete(timerId);
            now = timer.at;
            timer.callback();
            await flush();
        }
        now = end;
        await flush();
    }
    return { context, timers, calls, errors, advance, flush,
        read: expression => vm.runInContext(expression, context), get now() { return now; } };
}

test("accepted scrolling produces a prompt leading frame and a settled trailing frame", async () => {
    const h = harness();
    h.context.nookScheduleFrame({ inside: true });
    await h.advance(0);
    assert.deepEqual(h.calls, [0]);
    await h.advance(159);
    assert.deepEqual(h.calls, [0]);
    await h.advance(1);
    assert.deepEqual(h.calls, [0, 160]);
    assert.equal(h.timers.size, 0, "a settled screenshot must leave no polling timer running");
});

test("continuous scrolling is throttled and retains a final frame after the last event", async () => {
    const h = harness();
    for (let at = 0; at <= 600; at += 30) {
        await h.advance(at - h.now);
        h.context.nookScheduleFrame({ inside: true });
    }
    await h.advance(1000);
    assert.deepEqual(h.calls, [0, 220, 440, 660, 760]);
    assert.equal(h.timers.size, 0);
});

test("a finishing or disposed session cancels queued captures before touching the screen", async () => {
    for (const state of ["nookDisposed", "nookFinishing", "longRunning"]) {
        const h = harness();
        h.context.nookScheduleFrame({ inside: true });
        await h.advance(0);
        h.context[state] = state === "longRunning" ? false : true;
        await h.advance(1000);
        assert.deepEqual(h.calls, [0], state);
        assert.equal(h.timers.size, 0, state);
        h.context.nookScheduleFrame({ inside: true });
        assert.equal(h.timers.size, 0, `${state} rejects new native notifications`);
    }
});

test("input outside the selection or over its panel never schedules capture", async () => {
    const h = harness();
    h.context.nookScheduleFrame({ inside: false });
    h.context.nookScheduleFrame({ inside: true, panel: true });
    h.context.nookScheduleFrame(undefined);
    await h.advance(1000);
    assert.deepEqual(h.calls, []);
    assert.equal(h.timers.size, 0);
});

test("an externally busy stitch retries without dropping the last scroll frame", async () => {
    const h = harness();
    h.context.longBusy = true;
    h.context.nookScheduleFrame({ inside: true });
    await h.advance(200);
    assert.deepEqual(h.calls, []);
    assert.equal(h.timers.size, 1);
    h.context.longBusy = false;
    await h.advance(40);
    assert.deepEqual(h.calls, [240]);
    assert.equal(h.timers.size, 0, "one post-settle frame can satisfy both pending requests");
});

test("async frames never run concurrently and input arriving during capture is retained", async () => {
    const frames = [];
    let active = 0;
    let peak = 0;
    const h = harness({ capture: () => {
        active++;
        peak = Math.max(peak, active);
        const frame = deferred();
        frames.push(frame);
        return frame.promise.finally(() => { active--; });
    } });
    h.context.nookScheduleFrame({ inside: true });
    await h.advance(0);
    await h.advance(100);
    h.context.nookScheduleFrame({ inside: true });
    await h.advance(900);
    assert.deepEqual(h.calls, [0]);
    assert.equal(h.timers.size, 0, "in-flight work waits on completion instead of timer-spinning");
    frames[0].resolve();
    await h.flush();
    await h.advance(0);
    assert.deepEqual(h.calls, [0, 1000]);
    frames[1].resolve();
    await h.flush();
    assert.equal(peak, 1);
    assert.equal(h.timers.size, 0);
});

test("explicit cleanup cancels a tail and cannot be undone by an old async completion", async () => {
    const frame = deferred();
    const h = harness({ capture: () => frame.promise });
    h.context.nookScheduleFrame({ inside: true });
    await h.advance(0);
    h.context.nookClearScheduledFrame();
    frame.resolve();
    await h.flush();
    await h.advance(1000);
    assert.deepEqual(h.calls, [0]);
    assert.equal(h.timers.size, 0);
});

test("rejected capture reports once but stale rejection after cleanup cannot fail a new session", async () => {
    const liveFrame = deferred();
    const live = harness({ capture: () => liveFrame.promise });
    live.context.nookScheduleFrame({ inside: true });
    await live.advance(0);
    liveFrame.reject(new Error("synthetic native failure"));
    await live.flush();
    assert.equal(live.errors.length, 1);
    assert.match(live.errors[0].message, /synthetic native failure/);
    live.context.nookClearScheduledFrame();

    const staleFrame = deferred();
    const stale = harness({ capture: () => staleFrame.promise });
    stale.context.nookScheduleFrame({ inside: true });
    await stale.advance(0);
    stale.context.nookClearScheduledFrame();
    staleFrame.reject(new Error("cancelled native failure"));
    await stale.flush();
    await stale.advance(1000);
    assert.equal(stale.errors.length, 0);
    assert.deepEqual(stale.calls, [0]);
    assert.equal(stale.timers.size, 0);
});

function image(width = 4, height = 4, value = 0) {
    return { width, height, data: new Uint8ClampedArray(width * height * 4).fill(value) };
}

test("fallback observes scrollbar or middle-button scrolling even without pointer-filtered native events", async () => {
    const h = harness({ canCapture: () => false });
    h.context.nookStartFallbackFrames();
    h.context.nookStartFallbackFrames();
    assert.equal(h.timers.size, 1, "repeated start must not multiply polling loops");
    await h.advance(1600);
    assert.deepEqual(h.calls, [500, 1000, 1500]);
    assert.equal(h.timers.size, 1);
    h.context.nookClearScheduledFrame();
    await h.advance(1000);
    assert.deepEqual(h.calls, [500, 1000, 1500]);
    assert.equal(h.timers.size, 0);
});

test("fallback does not stack behind busy work, run during panel dragging, or compete with recent input", async () => {
    const frame = deferred();
    const h = harness({ capture: () => frame.promise });
    h.context.nookStartFallbackFrames();
    h.context.nookPanelDrag = {};
    await h.advance(500);
    assert.deepEqual(h.calls, []);
    h.context.nookPanelDrag = undefined;
    h.context.longBusy = true;
    await h.advance(500);
    assert.deepEqual(h.calls, []);
    h.context.longBusy = false;
    await h.advance(200);
    h.context.nookScheduleFrame({ inside: true });
    await h.advance(1800);
    assert.deepEqual(h.calls, [1200]);
    assert.equal(h.read("nookScheduledFramePending"), false, "fallback must not stack another pending frame while capture runs");
    frame.resolve();
    await h.flush();
    await h.advance(0);
    assert.deepEqual(h.calls, [1200, 3000], "only the genuine event tail survives busy work");
    h.context.nookClearScheduledFrame();
});

test("fallback timer terminates on finish, disposal and inactive sessions", async () => {
    for (const [flag, value] of [["nookFinishing", true], ["nookDisposed", true], ["longRunning", false]]) {
        const h = harness();
        h.context.nookStartFallbackFrames();
        h.context[flag] = value;
        await h.advance(1000);
        assert.deepEqual(h.calls, [], flag);
        assert.equal(h.timers.size, 0, flag);
    }
});

test("fingerprints compare every selected pixel, ignoring pixels outside the crop without retaining an image", async () => {
    const h = harness({ rect: [1, 1, 2, 2] });
    const frame = image();
    assert.equal(h.context.nookObserveFrame(frame), true);
    await h.advance(100);
    assert.equal(h.context.nookObserveFrame(image()), false);
    assert.equal(h.read("nookObservedFrameAt"), 100);
    assert.equal(h.read("nookChangedFrameAt"), 0);
    frame.data[0] = 255;
    assert.equal(h.context.nookObserveFrame(frame), false, "unrelated page/sidebar pixels must not prolong finishing");
    frame.data[(2 * 4 + 2) * 4 + 3] = 1;
    assert.equal(h.context.nookObserveFrame(frame), true, "even one tiny in-crop pixel or alpha change must count");
    assert.equal(h.read("nookChangedFrameAt"), 100);
    assert.match(h.read("nookObservedFrameHash"), /^[a-f0-9]{64}$/);
    h.context.finalRect = [0, 0, 4, 4];
    assert.equal(h.context.nookObserveFrame(frame), true, "crop identity participates in the fingerprint");
    assert.equal(h.context.nookObserveFrame(frame), false, "contiguous full-width crop follows the same deduplication rule");
});

test("invalid or changed physical capture dimensions fail before matching", () => {
    const h = harness();
    for (const rect of [[-1, 0, 4, 4], [0, 0, 5, 4], [0, 0, 0, 4], [0.5, 0, 3, 4]]) {
        h.context.finalRect = rect;
        assert.throws(() => h.context.nookObserveFrame(image()), /尺寸改变/);
    }
    h.context.finalRect = [0, 0, 4, 4];
    assert.throws(() => h.context.nookObserveFrame({ width: 4, height: 4, data: new Uint8ClampedArray(2) }), /尺寸改变/);
});

test("finish waits through delayed inertia and includes a truly stable final sampled screen", async () => {
    const values = [];
    const h = harness({ capture: (context, now) => {
        const value = now < 450 ? 1 : now < 850 ? 2 : 3;
        values.push(value);
        context.nookObserveFrame(image(4, 4, value));
        return Promise.resolve();
    } });
    h.context.nookFinishing = true;
    let result;
    const pending = h.context.nookFinishScrollFrames().then(value => { result = value; });
    await h.advance(900);
    assert.equal(result, undefined, "an inertia change at the end must postpone finish");
    await h.advance(800);
    await pending;
    assert.equal(result, true);
    assert.deepEqual([...new Set(values)], [1, 2, 3]);
    assert.ok(h.read("nookObservedFrameAt - nookChangedFrameAt") >= 400);
    assert.equal(h.timers.size, 0);
});

test("slow matching does not count as a second stable observation", async () => {
    const first = deferred();
    const h = harness({ capture: (context) => {
        context.nookObserveFrame(image(4, 4, h.calls.length));
        return h.calls.length === 1 ? first.promise : Promise.resolve();
    } });
    h.context.nookFinishing = true;
    let result;
    const pending = h.context.nookFinishScrollFrames().then(value => { result = value; });
    await h.advance(1000);
    assert.equal(result, undefined);
    first.resolve();
    await h.flush();
    await h.advance(25);
    assert.equal(result, undefined, "matcher latency cannot stand in for unchanged sampled pixels");
    assert.deepEqual(h.calls, [0]);
    await h.advance(200);
    assert.ok(h.calls.length >= 2, "a fresh physical sample is mandatory after slow matching");
    h.context.nookDisposed = true;
    await h.advance(200);
    await pending;
    assert.equal(result, false);
});

test("finish is bounded for continuously changing pages and leaves no hidden timer", async () => {
    const h = harness({ capture: context => {
        context.nookObserveFrame(image(4, 4, h.calls.length));
        return Promise.resolve();
    } });
    h.context.nookFinishing = true;
    const pending = h.context.nookFinishScrollFrames();
    await h.advance(6000);
    assert.equal(await pending, false);
    assert.ok(h.calls.length > 10 && h.calls.length < 40);
    assert.equal(h.timers.size, 0);
});

test("unchanged rejected overlap cannot finish as a silently truncated image", async () => {
    const h = harness({ capture: context => {
        context.nookObserveFrame(image(4, 4, 7));
        context.nookMatchUnresolved = true;
        return Promise.resolve();
    } });
    h.context.nookFinishing = true;
    const pending = h.context.nookFinishScrollFrames();
    await h.advance(6000);
    assert.equal(await pending, false);
    assert.equal(h.timers.size, 0);
    assert.equal(h.read("nookChangedFrameAt"), 0, "identical pixels alone are not proof of successful stitching");
});

test("a rejected overlap may recover and finish after a real stable accepted frame", async () => {
    const h = harness({ capture: (context, now) => {
        context.nookObserveFrame(image(4, 4, now < 800 ? 7 : 8));
        context.nookMatchUnresolved = now < 800;
        return Promise.resolve();
    } });
    h.context.nookFinishing = true;
    let result;
    const pending = h.context.nookFinishScrollFrames().then(value => { result = value; });
    await h.advance(800);
    assert.equal(result, undefined);
    await h.advance(800);
    await pending;
    assert.equal(result, true);
    assert.equal(h.timers.size, 0);
});

test("finish remains cancellable and bounded while a native request is stuck", async () => {
    for (const cancel of [false, true]) {
        const frame = deferred();
        const h = harness({ capture: () => frame.promise });
        h.context.nookFinishing = true;
        const pending = h.context.nookFinishScrollFrames();
        await h.advance(cancel ? 200 : 6000);
        if (cancel) { h.context.nookDisposed = true; await h.advance(25); }
        assert.equal(await pending, false);
        assert.deepEqual(h.calls, [0], "stuck native work must never be retried concurrently");
        assert.equal(h.timers.size, 0);
        frame.reject(new Error("late disposed frame"));
        await h.flush();
        assert.equal(h.errors.length, 0, "late rejected completion stays consumed");
    }
});

test("finish waits for prior busy work and capped partial images require no new frame", async () => {
    const h = harness({ capture: context => {
        context.nookObserveFrame(image());
        return Promise.resolve();
    } });
    h.context.nookFinishing = true;
    h.context.longBusy = true;
    const pending = h.context.nookFinishScrollFrames();
    await h.advance(300);
    assert.deepEqual(h.calls, []);
    h.context.longBusy = false;
    h.context.longCapped = true;
    await h.advance(25);
    assert.equal(await pending, true);
    assert.deepEqual(h.calls, []);
    assert.equal(h.timers.size, 0);
});
