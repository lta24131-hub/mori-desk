import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

export const UPSTREAM_COMMIT = "6f3f56241cccc8ada2c503df642566322dc67a3e";
const here = path.dirname(fileURLToPath(import.meta.url));

function replaceOnce(source, from, to, label) {
    if (source.split(from).length !== 2) throw new Error(`Upstream drift at ${label}`);
    return source.replace(from, to);
}

function replaceFunction(source, name, replacement) {
    const pattern = new RegExp(`^(?:async )?function ${name}\\(`, "m");
    const match = pattern.exec(source);
    if (!match) throw new Error(`Upstream function not found: ${name}`);
    const end = source.indexOf("\n}\n", match.index);
    if (end < 0) throw new Error(`Upstream function end not found: ${name}`);
    return source.slice(0, match.index) + replacement + source.slice(end + 2);
}

export function patchRenderer(original, adapter) {
    let source = original.replaceAll("\r\n", "\n");
    source = replaceOnce(source, "    Circle,\n", "    Circle,\n    Ellipse,\n    Textbox,\n", "Fabric Ellipse and Textbox imports");
    source = replaceOnce(source, 'import { ocrList } from "../ocr/ocr_omni";', 'const ocrList = (_store: unknown): {name: string | {text: string}; value: string}[] => [{ name: "Nook", value: "0" }];', "remove OCR runtime dependency");
    source = replaceFunction(source, "closeWin", 'async function closeWin() { nookIPC.send("nook-cancel"); nookDispose(); }');
    source = replaceFunction(source, "runCopy", 'async function runCopy() { await nookComplete("copy"); }');
    source = replaceFunction(source, "runSave", 'function runSave() { void nookComplete("save"); }');
    source = replaceFunction(source, "runDing", 'function runDing() { void nookComplete("pin"); }');
    source = replaceOnce(source, `function setEditType<T extends keyof EditType>(
    mainType: T,
    type: EditType[T],
): void {`, `function setEditType<T extends keyof EditType>(
    mainType: T,
    type: EditType[T],
): void {
    if (typeof nookFeaturePolicy !== "undefined" && !nookAllowsEditTool(mainType, type)) return;`, "guard all annotation buttons and hotkeys with captured feature policy");
    source = replaceOnce(source, "function setEditorP(zoom: number, x: number, y: number) {", `function setEditorP(zoom: number, x: number, y: number) {
    if (document.body.classList.contains("nook-result-editor")) {
        const position = nookConstrainResultPosition(zoom ?? editorP.zoom, x ?? editorP.x, y ?? editorP.y);
        x = position.x; y = position.y;
    }`, "result remains visible and keeps its scrollbar synchronized");
    source = replaceOnce(source, `const screenShots = initScreenShots(
    { c: store.get("额外截屏器.命令"), path: store.get("额外截屏器.位置") },
    undefined,
    t,
    store.get("使用XDGDesktopPortal"),
);`, "let screenShots: ReturnType<typeof initScreenShots>;", "lazy native screenshot runtime during one-shot warmup");
    source = replaceOnce(source, 'renderOn("clip_init", async ([_displays, imgBuffer, mainid, act]) => {', `renderOn("clip_init", async ([_displays, imgBuffer, mainid, act]) => {
    if (!nookBindRequest()) return;
    screenShots = initScreenShots(
        { c: store.get("额外截屏器.命令"), path: store.get("额外截屏器.位置") }, undefined, t, store.get("使用XDGDesktopPortal"),
    );`, "bind request before native initialization");
    source = replaceOnce(source, "const mouseBarW =", "let mouseBarW =", "refresh warm viewport-dependent coordinate hint");
    source = replaceOnce(source, "    const wx = await screenShots(_displays, imgBuffer);", "    const wx = await screenShots(undefined, imgBuffer);", "native physical monitor matching");
    source = replaceOnce(source, `editor.style({ width: \`\${window.screen.width / 全局缩放}px\` });
// 第一次截的一定是桌面,所以可提前定义
mainCanvas.width =
    clipCanvas.width =
    drawCanvas.width =
        window.screen.width * window.devicePixelRatio;
mainCanvas.height =
    clipCanvas.height =
    drawCanvas.height =
        window.screen.height * window.devicePixelRatio;`, `editor.style({ width: "1px" });
// Hidden warm renderers do not allocate full-screen backing stores.
mainCanvas.width = clipCanvas.width = drawCanvas.width = 1;
mainCanvas.height = clipCanvas.height = drawCanvas.height = 1;`, "small inactive canvas backing stores");
    source = replaceOnce(source, "async function setScreen(i: (typeof allScreens)[0]) {", `async function setScreen(i: (typeof allScreens)[0]) {
    editor.style({ width: \`\${i.size.width / i.scaleFactor}px\` });
    mouseBarW = Math.max(colorSize * colorISize,
        (String(Math.ceil(i.size.width / i.scaleFactor)).length + String(Math.ceil(i.size.height / i.scaleFactor)).length + 3) * 8) + 4;`, "active monitor owns editor width");
    source = replaceOnce(source, '    changeRightBar(false);\n});\n\nrenderOn("clip_stop_long"', '    changeRightBar(false);\n    await nookInitialize();\n});\n\nrenderOn("clip_stop_long"', "fixed initial crop and start");
    source = replaceFunction(source, "startLong", `async function startLong() {
    if (!nookRequest || nookDisposed || longRunning || longInited) return;
    nookFinishing = false;
    initLong(finalRect);
    await nookWaitForCapturePaint();
    await new Promise((resolve) => setTimeout(resolve, 60));
    if (nookDisposed) return;
    await long_s();
    if (nookDisposed) return;
    nookUpdatePointer();
    nookClearScheduledFrame();
    const nativeInput = require("uiohook-napi") as typeof import("uiohook-napi");
    uIOhook = nativeInput.uIOhook;
    if (!uIOhook) throw new Error("无法启动滚动监听，请重新截图。");
    const keys = nativeInput.UiohookKey;
    const scrollKeys: number[] = [keys.ArrowDown, keys.ArrowUp, keys.PageDown, keys.PageUp, keys.Home, keys.End, keys.Space];
    uIOhook.on("keydown", (event) => { if (scrollKeys.includes(event.keycode)) nookScheduleFrame(event); });
    uIOhook.on("keyup", nookScheduleFrame);
    uIOhook.on("wheel", nookScheduleFrame);
    uIOhook.start();
    nookStartFallbackFrames();
}`);
    source = replaceOnce(source, "    if (longBusy || longCapped) return;", "    if (nookDisposed || longBusy || longCapped || nookPanelDrag) return;", "late frame guard");
    source = replaceOnce(source, "        await addLong(\n            (await getNowScreen().capture()).toImageData() ?? undefined,\n        );", "        const frame = await nookCaptureFrame();\n        if (!nookDisposed && frame && nookObserveFrame(frame)) await addLong(frame);", "exclude helper overlay and unchanged live frames");
    source = replaceOnce(source, '        renderSend("windowIgnoreMouse", [false]);\n    } finally {\n        longBusy = false;', '        nookFail(error);\n    } finally {\n        longBusy = false;', "capture failure cleanup");
    source = replaceFunction(source, "initLong", `function initLong(rect: number[]) {
    longRunning = true;
    longInited = true;
    longCapped = false;
    longMouse = null;
    for (const i of longHide) i.style.display = "none";
    document.body.classList.remove("editor_bg");
    记忆框选值[nowScreenId] = [rect[0], rect[1], rect[2], rect[3]];
    store.set("框选.记忆.rects", 记忆框选值);
    lr.style({ left: \`\${rect[0] / ratio}px\`, top: \`\${rect[1] / ratio}px\`,
        width: \`\${rect[2] / ratio}px\`, height: \`\${rect[3] / ratio}px\` });
    nookStartPanel(rect);
}`);
    source = replaceFunction(source, "stopLong", `async function stopLong() {
    if (nookDisposed || nookFinishing || !longRunning) return;
    nookFinishing = true;
    nookClearScheduledFrame();
    nookFinishingPanel();
    clearInterval(longClipTime);
    try {
        const settled = await nookFinishScrollFrames();
        if (nookDisposed) return;
        if (!settled) {
            nookFinishing = false;
            nookResumePanel();
            nookStartFallbackFrames();
            return;
        }
        if (uIOhook) { uIOhook.removeAllListeners(); uIOhook.stop(); uIOhook = null; }
        lr.style({ opacity: "0" });
        await addLong(undefined);
        if (nookDisposed) return;
        nookPrepareResult();
        if (nookLimitReached)
            nookSetExportStatus("达到处理上限，已保留当前部分；可编辑、保存或复制。", false);
    } catch (error) { nookFail(error); }
    finally { nookFinishing = false; }
    // A resource cap means the result may be incomplete. Finalize and retain
    // it, but never mistake an automatic stop (or a cap hit while confirming
    // the user's last frame) for permission to output a truncated image.
    // A subsequent explicit panel action can still copy/save/edit this result.
    if (!nookDisposed && nookResultReady && !nookLimitReached) await nookRunResultAction(nookFinishAction);
}`);
    source = replaceOnce(source, "async function addLong(x: ImageData | undefined) {", "async function addLong(x: ImageData | undefined) {\n    if (nookDisposed) return;", "cancelled stitch guard");
    source = replaceOnce(source, "let longClipTime: NodeJS.Timeout;", "let longClipTime: NodeJS.Timeout | undefined;", "event driven capture has no interval timer");
    source = replaceOnce(source, '        finish: view().attr({ id: "long_finish" }),', '        finish: ele("button").attr({ id: "long_finish", type: "button" }),', "native accessible completion button");
    source = replaceOnce(source, '    newCanvas.style.maxHeight = "100%";', '    newCanvas.style.maxHeight = "none";\n    newCanvas.style.width = "100%";\n    newCanvas.style.height = "auto";', "readable width-fit long preview");
    source = replaceOnce(source, "    longPreview.add(newCanvas);", "    longPreview.add(newCanvas);\n    nookRefreshPanel(y < minY);", "persistent shell and live image progress");
    source = replaceOnce(source, "    const match = await longMatch(longX.lastImg, canvas);", "    const match = await longMatch(longX.lastImg, canvas);\n    if (nookDisposed) return;", "cancel during asynchronous matching");
    source = replaceOnce(source, "const m = await findOffset(", "const m = await nookFindOffset(", "validated scrolling offset");
    source = replaceOnce(source, "    );\n\n    // 图片应该反过来移动才能拼接上", "    );\n    if (!m) return { dx: 0, dy: 0, srcDX: 0, srcDY: 0, clipedImg: undefined };\n\n    // 图片应该反过来移动才能拼接上", "reject unmatched scroll frames without losing the last good image");
    source = replaceOnce(source, 'const dw = longFX === "xy" ? clip(img1.width) : 0;', 'const dw = longFX === "xy" ? Math.min(clip(img1.width), Math.max(0, img1.width - Math.abs(dx) - 16)) : 0;', "preserve actual horizontal overlap");
    source = replaceOnce(source, "const dh = clip(img1.height);", "const dh = Math.min(clip(img1.height), Math.max(0, img1.height - Math.abs(dy) - 16));", "preserve actual vertical overlap");
    source = replaceOnce(source, "        pjLong();\n        return;", "        await pjLong();\n        return;", "await editing transition");
    source = replaceOnce(source, "    // 前提：img大小一定小于等于最终拼接canvas\n    const newCanvas", `    if (nookDisposed) return;
    // Bounds are checked before creating or resizing an accumulated canvas.
    const predictedWidth = Math.max(longX.imgXY.x + (longX.img?.width || 0), x + img.width) - Math.min(longX.imgXY.x, x);
    const predictedHeight = Math.max(longX.imgXY.y + (longX.img?.height || 0), y + img.height) - Math.min(longX.imgXY.y, y);
    if (predictedWidth > 32768 || predictedHeight > 32768 || predictedWidth * predictedHeight > 64000000) {
        longCapped = true;
        nookLimitReached = true;
        setTimeout(() => { void stopLong(); }, 0);
        return;
    }
    const newCanvas`, "long image memory limit");
    source = replaceOnce(source, `    if (!newCtx) {
        longCapped = true;
        console.error("长截图：拿不到 2d 上下文，停止拼接");
        return;
    }`, `    if (!newCtx) {
        // Allocation failure can happen below the size cap. Do not silently
        // publish the last good image as if this new frame had been stitched.
        if (!longX.img) throw new Error("无法创建滚动截图，请关闭部分窗口后重新截图。");
        longCapped = true;
        nookLimitReached = true;
        setTimeout(() => { void stopLong(); }, 0);
        return;
    }`, "retain and label partial image after canvas allocation failure");
    source = replaceOnce(source, "function pjLong() {", "async function pjLong() {", "editable long image transition");
    source = replaceOnce(source, "    longRunning = false;\n}\n\n// 钉在屏幕上", "    longRunning = false;\n    await nookImportInitialAnnotations();\n}\n\n// 钉在屏幕上", "restore initial editable annotations");
    source = replaceOnce(source, "    if (longRunning) return;\n\n    document.body.classList.add", "    if (nookDisposed || nookFinishing || longRunning) return;\n\n    document.body.classList.add", "wheel state guard");
    source = replaceOnce(source, `    if ((nowType === "draw" || nowType === "shape") && !e.ctrlKey) {
        let v = strokeWidthEl.gv;
        v += e.deltaY / 50;
        v = Math.max(1, v);
        strokeWidthF.set(v);
        return;
    }
`, "", "result wheel always browses, never changes stroke properties");
    source = replaceOnce(source, "        const z = e.deltaY > 0 ? zoomW / zz : zoomW * zz;", "        const currentWidth = mainCanvas.width * editorP.zoom;\n        const z = Math.min(mainCanvas.width * 8, Math.max(mainCanvas.width * 0.03, e.deltaY > 0 ? currentWidth / zz : currentWidth * zz));", "zoom starts at actual fit and stays bounded");
    source = replaceOnce(source, `        const dx = nowMouseE.clientX - editorP.x * ozoom;
        const dy = nowMouseE.clientY - editorP.y * ozoom;
        const x = nowMouseE.clientX - dx * (nzoom / ozoom);
        const y = nowMouseE.clientY - dy * (nzoom / ozoom);`, `        const dx = e.clientX - editorP.x * ozoom;
        const dy = e.clientY - editorP.y * ozoom;
        const x = e.clientX - dx * (nzoom / ozoom);
        const y = e.clientY - dy * (nzoom / ozoom);`, "zoom anchors at current wheel event, not a stale capture pointer");
    source = replaceOnce(source, "document.onmousemove = (e) => {\n    nowMouseE = e;", "document.onmousemove = (e) => {\n    nowMouseE = e;\n    if (nookDisposed || nookFinishing || longRunning) return;", "skip hidden editor redraw during capture");
    source += `\n\n// Nook single-session adapter. Derived upstream screenshot/editor remains above.\n${adapter}\n`;
    return source;
}

export function prepare(upstream, stage) {
    upstream = path.resolve(upstream);
    stage = path.resolve(stage);
    const commit = execFileSync("git", ["rev-parse", "HEAD"], { cwd: upstream, encoding: "utf8", windowsHide: true }).trim();
    if (commit !== UPSTREAM_COMMIT) throw new Error(`Expected eSearch ${UPSTREAM_COMMIT}, got ${commit}`);
    if (stage === upstream || stage.startsWith(`${upstream}${path.sep}`) || upstream.startsWith(`${stage}${path.sep}`))
        throw new Error("Stage must not overlap the upstream checkout");
    if (fs.existsSync(stage) && fs.readdirSync(stage).length) throw new Error("Stage must be new or empty");
    fs.mkdirSync(stage, { recursive: true });
    for (const item of ["src", "lib", "assets", "LICENSE", "tsconfig.json", "tsconfig.node.json", "tsconfig.web.json"])
        fs.cpSync(path.join(upstream, item), path.join(stage, item), { recursive: true });
    const rendererPath = "src/renderer/clip/clip_window.ts";
    const adapter = ["renderer-panel.ts", "renderer-match.ts", "renderer-schedule.ts", "renderer-session.ts"].map((name) => fs.readFileSync(path.join(here, name), "utf8")).join("\n");
    const renderer = patchRenderer(fs.readFileSync(path.join(upstream, rendererPath), "utf8"), adapter);
    fs.writeFileSync(path.join(stage, rendererPath), renderer);
    const mainSource = fs.readFileSync(path.join(upstream, "src/main/main.ts"), "utf8").replaceAll("\r\n", "\n");
    const settingsStart = mainSource.indexOf("const defaultSetting: setting = {");
    const settingsEnd = mainSource.indexOf("\n};\ntry {\n    defaultSetting.保存", settingsStart);
    if (settingsStart < 0 || settingsEnd < 0) throw new Error("Upstream defaults extraction failed");
    let settings = mainSource.slice(settingsStart, settingsEnd + 3);
    settings = settings.replaceAll("app.getVersion()", '"15.6.0-nook"').replaceAll("matchBestLan()", '"zh-HANS"').replaceAll("isMac", "false");
    settings = settings.replace(/githubMirrorList\.api\[\s*Math\.floor\(Math\.random\(\) \* githubMirrorList\.api\.length\)\s*\]/g, '""');
    settings = settings.replace(/githubMirrorList\.base\[\s*Math\.floor\(Math\.random\(\) \* githubMirrorList\.base\.length\)\s*\]/g, '""');
    settings += '\ndefaultSetting.工具栏.功能 = ["close", "long", "ding", "copy", "save"];\ndefaultSetting.工具栏.按钮大小 = 36;\ndefaultSetting.保留截屏窗口 = false;\ndefaultSetting.更新.检查更新 = false;\ndefaultSetting.托盘 = "无";\ndefaultSetting.框选.识别窗口 = false;\ndefaultSetting.全局.模糊 = 0;\ndefaultSetting.全局.不透明度 = 0.97;\ndefaultSetting.全局.主题.light.barbg = "#fafafa";\ndefaultSetting.全局.主题.light.emphasis = "#ddd6fe";\ndefaultSetting.全局.主题.dark.barbg = "#202024";\ndefaultSetting.全局.主题.dark.emphasis = "#5b21b6";\nexport default defaultSetting;\n';
    fs.writeFileSync(path.join(stage, "src/main/nook-defaults.ts"), `import type { setting } from "../ShareTypes";\n${settings}`);
    for (const item of ["main.ts", "protocol.mjs", "protocol.d.mts", "panel-layout.mjs", "panel-layout.d.mts"])
        fs.copyFileSync(path.join(here, item), path.join(stage, "src/main", item));
    // Keep official icon source and license while bundling path-independent
    // data URLs for the native preview and toolbar documents.
    const nativeIcons = Object.fromEntries(Object.entries({ edit: "draw", save: "save", cancel: "close" }).map(([action, icon]) =>
        [action, `data:image/svg+xml;base64,${fs.readFileSync(path.join(upstream, "src/renderer/assets/icons", `${icon}.svg`)).toString("base64")}`]));
    nativeIcons.copy = `data:image/svg+xml;base64,${fs.readFileSync(path.join(here, "assets/fluent/checkmark.svg")).toString("base64")}`;
    fs.writeFileSync(path.join(stage, "src/main/nook-panel-icons.ts"), `export default ${JSON.stringify(nativeIcons)};\n`);
    fs.cpSync(path.join(here, "assets/fluent"), path.join(stage, "assets/fluent"), { recursive: true });
    fs.writeFileSync(path.join(stage, "electron.vite.config.ts"), `import {defineConfig} from "electron-vite";\nimport path from "node:path";\nexport default defineConfig({\n main:{build:{minify:false,rollupOptions:{input:path.resolve(__dirname,"src/main/main.ts")}}},\n renderer:{build:{rollupOptions:{input:{clip:path.resolve(__dirname,"src/renderer/capture.html")}},assetsInlineLimit:0,minify:false,sourcemap:false},define:{"process.platform":"process.platform"}}\n});\n`);
    const pkg = JSON.parse(fs.readFileSync(path.join(upstream, "package.json"), "utf8"));
    pkg.name = "nook-scroll-capture";
    pkg.version = "15.6.0-nook.1";
    pkg.description = "Nook single-session scrolling capture backend, derived from eSearch 15.6.0";
    pkg.main = "./out/main/main.js";
    pkg.scripts = { build: "electron-vite build", typecheck: "tsc -b --noEmit --force" };
    fs.writeFileSync(path.join(stage, "package.json"), `${JSON.stringify(pkg, null, 2)}\n`);
    for (const stylesheet of ["scroll-panel.css", "result-editor.css"])
        fs.appendFileSync(path.join(stage, "src/renderer/css/clip.css"), `\n${fs.readFileSync(path.join(here, stylesheet), "utf8")}\n`);
    fs.writeFileSync(path.join(stage, "NOOK-UPSTREAM.json"), `${JSON.stringify({ repository: "https://github.com/xushengfeng/eSearch", tag: "15.6.0", commit: UPSTREAM_COMMIT, modified: ["src/main/main.ts", rendererPath, "src/renderer/css/clip.css"], adapter: "modules/scroll-capture/host" }, null, 2)}\n`);
    return stage;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const args = process.argv.slice(2);
    const upstream = args[args.indexOf("--upstream") + 1];
    const stage = args[args.indexOf("--stage") + 1];
    if (!args.includes("--upstream") || !args.includes("--stage") || !upstream || !stage)
        throw new Error("Usage: node prepare.mjs --upstream <fixed checkout> --stage <new stage directory>");
    console.log(prepare(upstream, stage));
}
