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
        console,
        nookDisposed: false, nookFinishing: false, longRunning: true, longBusy: false,
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
    return { context, timers, calls, errors, advance, flush, get now() { return now; } };
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
