import assert from "node:assert/strict";
import fs from "node:fs";
import { createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import vm from "node:vm";
import { patchRenderer } from "../host/prepare.mjs";
import { writeExportRequest, writeResult, readExportReply } from "../host/protocol.mjs";
import { layoutCaptureControls } from "../host/panel-layout.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const upstream = process.env.NOOK_ESEARCH_SOURCE || path.resolve(here, "../../../outputs/esearch-15.6.0-source");
const require = createRequire(import.meta.url);
let ts;
try { ts = require("typescript"); }
catch { ts = require(path.join(upstream, "node_modules/typescript/lib/typescript.js")); }
const original = fs.readFileSync(path.join(upstream, "src/renderer/clip/clip_window.ts"), "utf8");
const adapter = ["renderer-panel.ts", "renderer-match.ts", "renderer-schedule.ts", "renderer-session.ts"]
    .map(name => fs.readFileSync(path.join(here, "../host", name), "utf8")).join("\n");
const parsed = ts.createSourceFile("clip_window.ts", patchRenderer(original, adapter), ts.ScriptTarget.Latest, true);
const functions = new Map(parsed.statements.filter(ts.isFunctionDeclaration).map(node => [node.name?.text, node.getText(parsed)]));
const names = ["nookAllowsFeature", "nookAllowsOutput", "nookAllowsEditTool", "nookHasAnnotations", "stopLong", "addLong", "longPutImg", "pjLong", "nookImportInitialAnnotations", "nookColor", "nookComplete",
    "nookSetExportStatus", "nookHandleExportReply", "nookPrepareResult", "nookRunResultAction", "nookRequestFinish",
    "nookEnterResultEditor", "nookResultTool", "nookDispose", "nookCancelCapture", "nookSendPanel", "nookFinishingPanel",
    "nookResumePanel", "nookMatchProblem", "nookHidePanel"];
for (const name of names) assert.ok(functions.has(name), `production flow function missing: ${name}`);
const script = ts.transpileModule(names.map(name => functions.get(name)).join("\n"), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;
const hostSource = fs.readFileSync(path.join(here, "../host/main.ts"), "utf8");
const hostAst = ts.createSourceFile("main.ts", hostSource, ts.ScriptTarget.Latest, true);
const hostPanelWiring = hostAst.statements.find(node => ts.isExpressionStatement(node) &&
    ts.isCallExpression(node.expression) && node.expression.expression.getText(hostAst) === "ipcMain.on" &&
    node.expression.arguments[0]?.text === "nook-panel-update");
assert.ok(hostPanelWiring, "production native panel-update receiver missing");
const hostPanelScript = ts.transpileModule(hostPanelWiring.getText(hostAst), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;

// Run the real host receiver as well as the real renderer sender. Its field
// bounds and resume/finish lock rules are not reimplemented in this fixture.
function hostPanelReceiver() {
    let handler;
    const states = [];
    const context = vm.createContext({
        nookFeaturePolicy: undefined,
        ipcMain: { on(name, callback) { assert.equal(name, "nook-panel-update"); handler = callback; } },
        trusted: () => true, terminal: false, cancelAfterExport: false, resultEditor: false, panelWin: {}, pendingExport: null,
        panelFinishPending: false, panelWaitTimer: null, showPanelWaitNotice: false, clearTimeout() {},
        panelState: { status: "", title: "", finishEnabled: false },
        publishPanel: () => states.push({ ...context.panelState }),
    });
    vm.runInContext(hostPanelScript, context);
    return { accept: value => handler({}, value), context, states };
}
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aavsAAAAASUVORK5CYII=", "base64");
function deferred() { let resolve, reject; const promise = new Promise((r, j) => { resolve = r; reject = j; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 20; i++) await Promise.resolve(); }

// Exercise the composed production renderer, including the upstream final
// compositor and initial annotation migration. Only native IPC, pixel encoding,
// Fabric primitives and DOM are replaced. The real export protocol writes to a
// fresh temporary directory; no app, screen, clipboard or personal library runs.
function harness(t, options = {}) {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nook-scroll-reference-flow-"));
    t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const events = [], exports = [], marks = [], calls = [], elements = [], scheduled = [];
    const hostPanel = hostPanelReceiver();
    const request = { schemaVersion: 1, sessionId: "cafe0123456789abcdef0123456789ab", outputDirectory: directory,
        transactionalOutputs: true, annotationsClip: { x: 0, y: 0, width: 200, height: 300 },
        initialAnnotations: options.annotations ?? [{ kind: "rect", x1: 10, y1: 20, x2: 40, y2: 60, color: "#ff0000", strokeWidth: 3 }] };
    const stitchedPixels = { tag: "complete stitched image", width: 200, height: 900 };
    const node = (tag) => {
        const element = { tag, style: {}, children: [], append(...children) { this.children.push(...children); },
            addEventListener() {}, setAttribute(name, value) { this[name] = value; }, remove() {} };
        elements.push(element); return element;
    };
    const body = node("body");
    const classes = new Set();
    body.classList = { add: value => classes.add(value), remove: value => classes.delete(value), toggle: (value, enabled) => enabled ? classes.add(value) : classes.delete(value) };
    class FabricObject { constructor(...values) { this.arguments = values; } set(values) { Object.assign(this, values); } }
    const mainCanvas = { width: 200, height: 300, getContext: () => ({ putImageData: value => { calls.push("pixels-finalized"); mainCanvas.pixels = value; } }) };
    let context;
    const ipc = {
        send(channel, ...args) {
            events.push([channel, ...args]);
            if (channel === "nook-panel-update") hostPanel.accept(args[0]);
            if (channel === "nook-complete") {
                const record = writeExportRequest(request, exports.length + 1, args[0], args[1]);
                exports.push(record);
            }
            if (channel === "nook-cancel") writeResult(request, "cancelled");
        },
        sendSync(channel, value) {
            events.push([channel, value]);
            if (channel === "nook-result-ready") {
                assert.equal(mainCanvas.pixels, stitchedPixels);
                assert.equal(marks.length, request.initialAnnotations.length, "snapshot cannot be ready before all initial annotations are restored");
                return options.readyFails ? null : { ready: true };
            }
            if (channel === "nook-result-editor") return options.editorFails ? null : { x: 10, y: 10, width: 800, height: 600 };
            throw new Error(`Unexpected synchronous channel: ${channel}`);
        },
    };
    context = vm.createContext({
        console, Buffer, nookIPC: ipc, nookRequest: request, nookFeaturePolicy: options.features,
        nookDisposed: false, nookFinishing: false, nookExporting: false, nookResultReady: false, nookResultMode: false,
        nookInitialImported: false, nookLimitReached: false, nookResultStatus: undefined, nookFinishAction: "copy",
        nookResultScroll: undefined, nookResultScrollContent: undefined,
        nookPanelOpen: true, nookMatchUnresolved: false, longRunning: true, longCapped: false, longClipTime: null, longMouse: null,
        longX: { img: { width: 200, height: 900, getContext: () => ({ getImageData: () => stitchedPixels }) }, imgXY: { x: 0, y: 0 } },
        longHide: [{ style: { display: "none" } }], longPreview: { style() {}, clear() {} },
        mainCanvas, clipCanvas: {}, drawCanvas: {}, finalRect: [0, 0, 200, 300], ratio: 1,
        uIOhook: { removeAllListeners: () => calls.push("remove-input"), stop: () => calls.push("stop-input") },
        document: { body, createElement: node }, window: { innerWidth: 800, innerHeight: 600,
            addEventListener: name => calls.push(`window-${name}`) },
        Rect: FabricObject, Ellipse: FabricObject, Line: FabricObject, arrow: FabricObject,
        IText: FabricObject, Textbox: FabricObject, Circle: FabricObject, Polyline: FabricObject,
        FabricImage: { fromURL: async () => { if (options.annotationGate) await options.annotationGate.promise; return new FabricObject(); } },
        require(name) { assert.equal(name, "node:fs"); return { lstatSync: () => ({ isFile: () => true, isSymbolicLink: () => false, size: 1 }), readFileSync: () => png }; },
        fabricCanvas: { setDimensions() {}, add: (...objects) => marks.push(...objects), requestRenderAll() {},
            discardActiveObject() {}, renderAll() {} },
        toolsX: { long: { el: { el: { style: {} } } } },
        toolBarEl: { el: node("toolbar") },
        iconEl: () => ({ el: node("icon") }),
        lr: { style() {} }, clearInterval() {},
        ele: () => ({ el: { getContext: () => null } }),
        setTimeout: callback => { scheduled.push(callback); return scheduled.length; },
        nookClearScheduledFrame: () => calls.push("clear-schedule"),
        nookFinishScrollFrames: async () => {
            if (options.limitDuringSettle) context.nookLimitReached = true;
            if (options.allocationDuringSettle) context.longPutImg({ width: 200, height: 300 }, 0, 900);
            return options.settleGate ? options.settleGate.promise : true;
        },
        nookStartFallbackFrames: () => calls.push("restart-capture"),
        nookRefreshPanel: () => calls.push("refresh-panel"),
        nookPanelPreview: () => "data:image/png;base64,AA==",
        renderSend: (...args) => calls.push(args), followBar: () => calls.push("editor-toolbar"),
        drawClipRect() {}, hisPush: () => calls.push("annotation-history"), setEditType() {}, setEditorP() {},
        requestAnimationFrame: callback => { callback(); return 1; },
        nookWaitForCapturePaint: async () => {},
        getClipPhoto: () => {
            assert.deepEqual(Array.from(context.finalRect), [0, 0, 200, 900], "output crop must include every row, not the original first-screen selection");
            assert.equal(mainCanvas.width, 200);
            assert.equal(mainCanvas.height, 900);
            return { pixels: mainCanvas.pixels, marks: [...marks] };
        },
        getBlob: async (image, mime) => {
            assert.equal(mime, "image/png");
            assert.equal(image.pixels, stitchedPixels);
            assert.equal(image.marks.length, request.initialAnnotations.length);
            calls.push("encoded-complete-image");
            if (options.blobGate) await options.blobGate.promise;
            if (options.encodeFails?.()) throw new Error("synthetic encoder failure");
            return { arrayBuffer: async () => { if (options.bufferGate) await options.bufferGate.promise; return png; } };
        },
        nookFail: error => { throw error; },
    });
    vm.runInContext(script, context);
    return { context, directory, events, exports, calls, marks, elements, scheduled, mainCanvas, hostPanel, captureRequest: request,
        request: action => context.nookRequestFinish(action),
        reply: (status, message = "") => context.nookHandleExportReply(status, message),
        channels: channel => events.filter(event => event[0] === channel),
        lastPanel: () => events.filter(event => event[0] === "nook-panel-update").at(-1)?.[1],
    };
}

test("reference Copy and Save finalize all pixels and annotations without opening an editor", async t => {
    for (const action of ["copy", "save"]) {
        const h = harness(t);
        await h.request(action);
        assert.equal(h.context.longRunning, false);
        assert.equal(h.context.nookResultReady, true);
        assert.equal(h.exports.length, 1);
        assert.equal(h.exports[0].action, action);
        assert.equal(h.channels("nook-result-ready").length, 1);
        assert.equal(h.channels("nook-result-editor").length, 0);
        assert.equal(h.channels("nook-panel-close").length, 0, "output awaits parent acknowledgement with its retry controls retained");
        assert.equal(h.elements.some(element => element.tag === "header"), false);
        assert.equal(h.context.nookExporting, true);
    }
});

test("result editor omits unchecked annotation tools while preserving navigation and outputs", async t => {
    const features = Object.fromEntries(["rectangle", "ellipse", "lines", "pen", "text", "mosaic"].map(name => [name, false]));
    const basic = harness(t, { features, annotations: [] });
    await basic.request("edit");
    assert.equal(basic.context.nookResultMode, true);
    assert.equal(basic.elements.find(element => element.id === "nook_result_tools").children.length, 0);
    assert.ok(basic.elements.some(element => element.id === "nook_result_scroll"));
    assert.ok(basic.elements.some(element => element.title === "退出截图（Esc）"));
    const textOnly = harness(t, { features: { ...features, text: true }, annotations: [] });
    await textOnly.request("edit");
    const labels = textOnly.elements.find(element => element.id === "nook_result_tools").children.map(element => element.title);
    assert.deepEqual(labels, ["选择并移动标注", "文字", "撤销（Ctrl + Z）", "重做（Ctrl + Y）", "颜色、粗细与更多工具"]);
});

test("reference output waits for asynchronous annotation import and suppresses competing actions", async t => {
    const annotationGate = deferred();
    const h = harness(t, { annotationGate, annotations: [{ kind: "mosaic", x1: 0, y1: 0, x2: 10, y2: 10, color: "#000000", strokeWidth: 1, mosaicPath: "mark-0.png" }] });
    const pending = h.request("copy");
    await flush();
    assert.equal(h.exports.length, 0);
    assert.equal(h.channels("nook-result-ready").length, 0);
    await h.request("edit");
    await h.request("save");
    annotationGate.resolve();
    await pending;
    assert.equal(h.exports.length, 1);
    assert.equal(h.exports[0].action, "copy", "the first finish action owns finalization");
    assert.equal(h.marks.length, 1);
    assert.equal(h.channels("nook-result-editor").length, 0);
});

test("automatic limits and caps reached during finishing retain the image until a new explicit action", async t => {
    for (const trigger of ["automatic", "copy", "save", "edit"]) {
        for (const nextAction of ["copy", "save", "edit"]) {
            const h = harness(t, { limitDuringSettle: trigger !== "automatic" });
            if (trigger === "automatic") {
                h.context.nookLimitReached = true;
                await h.context.stopLong();
            } else await h.request(trigger);
            assert.equal(h.context.longRunning, false);
            assert.equal(h.context.nookResultReady, true);
            assert.equal(h.context.nookFinishing, false);
            assert.equal(h.context.nookExporting, false);
            assert.equal(h.exports.length, 0, `${trigger} must not silently output a resource-limited image`);
            assert.equal(h.channels("nook-result-editor").length, 0, `${trigger} must first show the cap warning`);
            assert.equal(h.channels("nook-result-ready").length, 1);
            assert.match(h.lastPanel().status, /上限/);
            assert.equal(h.lastPanel().resume, true);
            assert.equal(h.lastPanel().finishEnabled, true);
            assert.equal(h.calls.includes("restart-capture"), false);

            await h.request(nextAction);
            if (nextAction === "edit") {
                assert.equal(h.exports.length, 0);
                assert.equal(h.channels("nook-result-editor").length, 1);
                assert.equal(h.calls.includes("editor-toolbar"), true);
            } else {
                assert.equal(h.exports.length, 1);
                assert.equal(h.exports[0].action, nextAction);
                assert.equal(h.channels("nook-result-editor").length, 0);
            }
            assert.equal(h.marks.length, 1, "explicit follow-up reuses, rather than duplicates, the finalized annotations");
            assert.equal(h.calls.filter(call => call === "pixels-finalized").length, 1);
            assert.equal(h.channels("nook-result-ready").length, 1);
        }
    }
});

test("a failed accumulated canvas retains only the stitched part and requires a new explicit output action", async t => {
    for (const trigger of ["copy", "save", "edit", "automatic"]) {
        for (const nextAction of ["copy", "save", "edit"]) {
            const h = harness(t, { allocationDuringSettle: trigger !== "automatic" });
            const retained = h.context.longX.img;
            if (trigger === "automatic") {
                h.context.longPutImg({ width: 200, height: 300 }, 0, 900);
                assert.equal(h.scheduled.length, 1, "allocation failure must stop capture instead of leaving a dead preview");
                h.scheduled.shift()();
                await flush();
            } else await h.request(trigger);

            assert.equal(h.exports.length, 0, `${trigger} must not silently output a truncated image`);
            assert.equal(h.channels("nook-result-editor").length, 0, "the partial-result warning precedes any editor transition");
            assert.equal(h.context.longX.img, retained, "the failed new canvas cannot replace the last good image");
            assert.equal(h.mainCanvas.height, 900, "the unmerged final 300 pixels must not count as completed content");
            assert.equal(h.context.longRunning, false);
            assert.equal(h.context.nookResultReady, true);
            assert.equal(h.context.nookExporting, false);
            assert.equal(h.hostPanel.context.panelState.finishEnabled, true);
            assert.match(h.lastPanel().status, /已保留当前部分/);

            // A queued stop from a frame completed during finishing must be inert.
            for (const callback of h.scheduled.splice(0)) callback();
            await flush();
            assert.equal(h.exports.length, 0);
            await h.request(nextAction);
            if (nextAction === "edit") {
                assert.equal(h.exports.length, 0);
                assert.equal(h.channels("nook-result-editor").length, 1);
                assert.equal(h.calls.includes("editor-toolbar"), true);
            } else {
                assert.equal(h.exports.length, 1);
                assert.equal(h.exports[0].action, nextAction);
            }
            assert.equal(h.marks.length, 1);
            assert.equal(h.calls.filter(call => call === "pixels-finalized").length, 1);
            assert.equal(h.channels("nook-result-ready").length, 1);
        }
    }
});

test("cancelled Save and failed Copy retry the retained image then allow Edit without recapture", async t => {
    const h = harness(t);
    await h.request("save");
    h.reply("cancelled");
    assert.equal(h.context.nookExporting, false);
    assert.equal(h.lastPanel().resume, true);
    assert.equal(h.lastPanel().finishEnabled, true);
    assert.equal(h.channels("nook-result-editor").length, 0);
    await h.request("copy");
    assert.deepEqual(h.exports.map(value => value.action), ["save", "copy"]);
    h.reply("error", "clipboard temporarily busy");
    assert.equal(h.lastPanel().resume, true);
    await h.request("edit");
    assert.equal(h.channels("nook-result-editor").length, 1);
    assert.equal(h.exports.length, 2);
    assert.equal(h.marks.length, 1, "retry and edit cannot duplicate inherited annotations");
    assert.equal(h.channels("nook-result-ready").length, 1);
    assert.equal(h.calls.filter(call => call === "stop-input").length, 1);
    assert.equal(h.calls.includes("restart-capture"), false);
    assert.equal(h.elements.filter(element => element.tag === "header").length, 1);
    assert.equal(h.calls.includes("editor-toolbar"), true, "Edit must finish its transition, not swallow a partial DOM setup failure");
    assert.equal(h.calls.includes("window-resize"), true);
});

test("a native long-path save error passes panel bounds and restores actual host retry controls", async t => {
    const h = harness(t);
    await h.request("save");
    assert.equal(h.hostPanel.context.panelState.finishEnabled, false, "native controls start disabled while output is pending");
    const nativeMessage = `无法写入文件 C:\\${"项目素材\\".repeat(400)}capture.png：拒绝访问`;
    fs.writeFileSync(path.join(h.directory, "export-1.reply.json"), JSON.stringify({ schemaVersion: 1,
        sessionId: h.captureRequest.sessionId, exportId: 1, status: "error", message: nativeMessage }));
    const reply = readExportReply(h.captureRequest, 1);
    assert.equal(reply.message.length, 1600, "exercise the real parent reply protocol's maximum message");
    h.reply(reply.status, reply.message);
    assert.equal(h.hostPanel.context.panelState.finishEnabled, true, "recoverable native failure must re-enable the actual host buttons");
    assert.equal(h.lastPanel().resume, true);
    assert.ok(h.lastPanel().status.length <= 240);
    assert.ok(h.lastPanel().title.length <= 500);
    assert.match(h.hostPanel.context.panelState.status, /^未完成：无法写入文件/);
    assert.equal(h.context.nookExporting, false);
    assert.equal(h.context.nookDisposed, false);
    await h.request("copy");
    assert.deepEqual(h.exports.map(value => value.action), ["save", "copy"]);
    assert.equal(h.calls.filter(call => call === "pixels-finalized").length, 1, "retry uses the same complete snapshot");
});

test("Escape during either image encoding await cannot publish a late export or revive controls", async t => {
    for (const waitStage of ["blobGate", "bufferGate"]) {
        const gate = deferred();
        const h = harness(t, { [waitStage]: gate });
        const pending = h.request("copy");
        await flush();
        assert.equal(h.calls.includes("encoded-complete-image"), true);
        h.context.nookCancelCapture();
        gate.resolve();
        await pending;
        h.reply("cancelled");
        assert.equal(h.exports.length, 0);
        assert.equal(h.channels("nook-complete").length, 0);
        assert.equal(h.channels("nook-cancel").length, 1);
        assert.equal(JSON.parse(fs.readFileSync(path.join(h.directory, "result.json"))).status, "cancelled");
        assert.equal(h.context.nookDisposed, true);
        assert.equal(h.calls.includes("restart-capture"), false);
    }
});

test("an encoder failure preserves a finalized snapshot and permits a fresh output request", async t => {
    let fail = true;
    const h = harness(t, { encodeFails: () => fail });
    await h.request("save");
    assert.equal(h.exports.length, 0);
    assert.equal(h.context.nookDisposed, false);
    assert.equal(h.context.nookExporting, false);
    assert.equal(h.context.nookResultReady, true);
    assert.equal(h.lastPanel().resume, true);
    fail = false;
    await h.request("copy");
    assert.equal(h.exports.length, 1);
    assert.equal(h.exports[0].action, "copy");
    assert.equal(h.calls.filter(call => call === "pixels-finalized").length, 1);
    assert.equal(h.channels("nook-result-editor").length, 0);
});

test("a refused Edit transition leaves the snapshot available to Copy instead of trapping an empty editor", async t => {
    const h = harness(t, { editorFails: true });
    await h.request("edit");
    assert.equal(h.context.nookResultReady, true);
    assert.equal(h.context.nookResultMode, false);
    assert.equal(h.elements.some(element => element.tag === "header"), false);
    assert.equal(h.channels("nook-panel-close").length, 0);
    assert.equal(h.lastPanel().resume, true);
    await h.request("copy");
    assert.equal(h.exports.length, 1);
    assert.equal(h.exports[0].action, "copy");
    assert.equal(h.marks.length, 1);
    assert.equal(h.calls.filter(call => call === "pixels-finalized").length, 1);
});

test("reference layout matrix keeps controls contained, separated and stable across image growth", () => {
    // These crops are expressed in source-monitor desktop DIP, including
    // physical-to-DIP conversions at 125%, 150% and 200%; no desktop is read.
    const matrix = [
        { area: { x: 0, y: 0, width: 1920, height: 1040 }, crop: { x: 110, y: 90, width: 900, height: 760 } },
        { area: { x: -1280, y: -720, width: 1280, height: 680 }, crop: { x: -1200, y: -700, width: 800 / 1.5, height: 600 / 1.5 } },
        { area: { x: 1536, y: -160, width: 1536, height: 824 }, crop: { x: 1536 + 800 / 1.25, y: -160 + 40, width: 1000 / 1.25, height: 900 / 1.25 } },
        { area: { x: 0, y: -1080, width: 1920, height: 1040 }, crop: { x: 0, y: -1080, width: 3840 / 2, height: 2160 / 2 } },
        { area: { x: -1920, y: 0, width: 1920, height: 1040 }, crop: { x: -1920, y: 0, width: 1920, height: 1080 } },
        { area: { x: 0, y: 40, width: 800, height: 560 }, crop: { x: 0, y: 0, width: 800, height: 600 } },
        { area: { x: 0, y: 0, width: 1280, height: 680 }, crop: { x: 0, y: 0, width: 1280, height: 300 } },
    ];
    const overlaps = (a, b) => a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y;
    for (const { area, crop } of matrix) {
        let firstToolbar;
        for (const multiplier of [1, 3, 20]) {
            const imageWidth = 1600, imageHeight = 1000 * multiplier;
            const result = layoutCaptureControls({ selection: crop, workArea: area, imageWidth, imageHeight });
            for (const [name, rect] of Object.entries({ toolbar: result.toolbar, preview: result.preview })) {
                assert.ok(Object.values(rect).every(Number.isFinite), `${name} must remain finite`);
                assert.ok(rect.width > 0 && rect.height > 0, `${name} needs positive size`);
                assert.ok(rect.x >= area.x && rect.y >= area.y, `${name} cannot leave the source monitor start`);
                assert.ok(rect.x + rect.width <= area.x + area.width && rect.y + rect.height <= area.y + area.height,
                    `${name} cannot leave the source monitor end`);
            }
            if (!firstToolbar) firstToolbar = result.toolbar;
            assert.deepEqual(result.toolbar, firstToolbar, "image growth must never move its action targets");
            const margin = Math.min(8, Math.floor(Math.min(area.width, area.height) / 4));
            const expectedBottom = Math.round(Math.max(area.y + margin + 1, Math.min(crop.y + crop.height, area.y + area.height - margin)));
            assert.equal(result.preview.y + result.preview.height, expectedBottom, "the complete preview is bottom anchored");
            assert.ok(Math.abs(result.preview.width - imageWidth * result.scale) < 1.01);
            assert.ok(Math.abs(result.preview.height - imageHeight * result.scale) < 1.01);
            assert.equal(overlaps(result.preview, result.toolbar), false,
                `preview and toolbar overlap at ${JSON.stringify({ area, crop, multiplier, result })}`);
        }
    }
});
