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
    source = replaceOnce(source, "    Circle,\n", "    Circle,\n    Ellipse,\n", "Fabric Ellipse import");
    source = replaceOnce(source, 'import { ocrList } from "../ocr/ocr_omni";', 'const ocrList = (_store: unknown): {name: string | {text: string}; value: string}[] => [{ name: "Nook", value: "0" }];', "remove OCR runtime dependency");
    source = replaceFunction(source, "closeWin", 'async function closeWin() { nookIPC.send("nook-cancel"); nookDispose(); }');
    source = replaceFunction(source, "runCopy", 'async function runCopy() { await nookComplete("copy"); }');
    source = replaceFunction(source, "runSave", 'function runSave() { void nookComplete("save"); }');
    source = replaceFunction(source, "runDing", 'function runDing() { void nookComplete("pin"); }');
    source = replaceOnce(source, "    const wx = await screenShots(_displays, imgBuffer);", "    const wx = await screenShots(undefined, imgBuffer);", "native physical monitor matching");
    source = replaceOnce(source, '    changeRightBar(false);\n});\n\nrenderOn("clip_stop_long"', '    changeRightBar(false);\n    await nookInitialize();\n});\n\nrenderOn("clip_stop_long"', "fixed initial crop and start");
    source = replaceFunction(source, "startLong", `async function startLong() {
    if (nookDisposed || longRunning || longInited) return;
    nookFinishing = false;
    initLong(finalRect);
    await new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    await new Promise((resolve) => setTimeout(resolve, 60));
    if (nookDisposed) return;
    await long_s();
    if (nookDisposed) return;
    nookUpdatePointer();
    nookClearScheduledFrame();
    uIOhook = require("uiohook-napi").uIOhook;
    if (!uIOhook) throw new Error("无法启动滚动监听，请重新截图。");
    uIOhook.on("keyup", nookScheduleFrame);
    uIOhook.on("wheel", nookScheduleFrame);
    uIOhook.start();
}`);
    source = replaceOnce(source, "    if (longBusy || longCapped) return;", "    if (nookDisposed || longBusy || longCapped) return;", "late frame guard");
    source = replaceOnce(source, "(await getNowScreen().capture()).toImageData()", "await nookCaptureFrame()", "exclude helper overlay from live frames");
    source = replaceOnce(source, '        renderSend("windowIgnoreMouse", [false]);\n    } finally {\n        longBusy = false;', '        nookFail(error);\n    } finally {\n        longBusy = false;', "capture failure cleanup");
    source = replaceFunction(source, "initLong", `function initLong(rect: number[]) {
    longRunning = true;
    longInited = true;
    longCapped = false;
    longMouse = setInterval(nookUpdatePointer, 50);
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
    if (uIOhook) { uIOhook.removeAllListeners(); uIOhook.stop(); uIOhook = null; }
    clearInterval(longClipTime);
    try {
        const deadline = Date.now() + 10000;
        while (longBusy && !nookDisposed) {
            if (Date.now() > deadline) throw new Error("长截图拼接超时，请重试。");
            await new Promise((resolve) => setTimeout(resolve, 25));
        }
        if (nookDisposed) return;
        await long_s();
        if (nookDisposed) return;
        lr.style({ opacity: "0" });
        renderSend("windowIgnoreMouse", [false]);
        await addLong(undefined);
        if (nookDisposed) return;
        nookHidePanel();
        for (const i of longHide) i.style.display = "";
        followBar();
    } catch (error) { nookFail(error); }
    finally { nookFinishing = false; }
}`);
    source = replaceOnce(source, "async function addLong(x: ImageData | undefined) {", "async function addLong(x: ImageData | undefined) {\n    if (nookDisposed) return;", "cancelled stitch guard");
    source = replaceOnce(source, "let longClipTime: NodeJS.Timeout;", "let longClipTime: NodeJS.Timeout | undefined;", "event driven capture has no interval timer");
    source = replaceOnce(source, '        finish: view().attr({ id: "long_finish" }),', '        finish: ele("button").attr({ id: "long_finish", type: "button" }),', "native accessible completion button");
    source = replaceOnce(source, '    newCanvas.style.maxHeight = "100%";', '    newCanvas.style.maxHeight = "none";\n    newCanvas.style.width = "100%";\n    newCanvas.style.height = "auto";', "readable width-fit long preview");
    source = replaceOnce(source, "    longPreview.add(newCanvas);", "    longPreview.add(newCanvas);\n    nookRefreshPanel(y < minY);", "persistent shell and live image progress");
    source = replaceOnce(source, "    const match = await longMatch(longX.lastImg, canvas);", "    const match = await longMatch(longX.lastImg, canvas);\n    if (nookDisposed) return;", "cancel during asynchronous matching");
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
    source = replaceOnce(source, "function pjLong() {", "async function pjLong() {", "editable long image transition");
    source = replaceOnce(source, "    longRunning = false;\n}\n\n// 钉在屏幕上", "    longRunning = false;\n    await nookImportInitialAnnotations();\n}\n\n// 钉在屏幕上", "restore initial editable annotations");
    source = replaceOnce(source, "    if (longRunning) return;\n\n    document.body.classList.add", "    if (nookDisposed || nookFinishing || longRunning) return;\n\n    document.body.classList.add", "wheel state guard");
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
    const adapter = ["renderer-panel.ts", "renderer-schedule.ts", "renderer-session.ts"].map((name) => fs.readFileSync(path.join(here, name), "utf8")).join("\n");
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
    for (const item of ["main.ts", "protocol.mjs", "protocol.d.mts"])
        fs.copyFileSync(path.join(here, item), path.join(stage, "src/main", item));
    fs.writeFileSync(path.join(stage, "electron.vite.config.ts"), `import {defineConfig} from "electron-vite";\nimport path from "node:path";\nexport default defineConfig({\n main:{build:{minify:false,rollupOptions:{input:path.resolve(__dirname,"src/main/main.ts")}}},\n renderer:{build:{rollupOptions:{input:{clip:path.resolve(__dirname,"src/renderer/capture.html")}},assetsInlineLimit:0,minify:false,sourcemap:false},define:{"process.platform":"process.platform"}}\n});\n`);
    const pkg = JSON.parse(fs.readFileSync(path.join(upstream, "package.json"), "utf8"));
    pkg.name = "nook-scroll-capture";
    pkg.version = "15.6.0-nook.1";
    pkg.description = "Nook single-session scrolling capture backend, derived from eSearch 15.6.0";
    pkg.main = "./out/main/main.js";
    pkg.scripts = { build: "electron-vite build", typecheck: "tsc -b --noEmit --force" };
    fs.writeFileSync(path.join(stage, "package.json"), `${JSON.stringify(pkg, null, 2)}\n`);
    fs.appendFileSync(path.join(stage, "src/renderer/css/clip.css"), `\n${fs.readFileSync(path.join(here, "scroll-panel.css"), "utf8")}\n`);
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
