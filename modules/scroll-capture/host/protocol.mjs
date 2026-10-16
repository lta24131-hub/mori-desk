import fs from "node:fs";
import path from "node:path";

export const SCHEMA_VERSION = 1;
export const ACTIONS = Object.freeze(["copy", "save", "pin", "collect"]);
export const MAX_PIXELS = 64_000_000;
export const MAX_PNG_BYTES = 256 * 1024 * 1024;
const sessionPattern = /^[a-f0-9]{32}$/i;

function integer(value, name, minimum = -200_000, maximum = 200_000) {
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum)
        throw new Error(`Invalid ${name}`);
    return value;
}

export function validateRequest(raw, requestPath) {
    if (!raw || typeof raw !== "object" || Array.isArray(raw))
        throw new Error("Invalid request object");
    if (raw.schemaVersion !== SCHEMA_VERSION) throw new Error("Unsupported schemaVersion");
    if (typeof raw.sessionId !== "string" || !sessionPattern.test(raw.sessionId))
        throw new Error("Invalid sessionId");
    if (typeof raw.outputDirectory !== "string" || !path.isAbsolute(raw.outputDirectory))
        throw new Error("outputDirectory must be absolute");
    const outputDirectory = path.resolve(raw.outputDirectory);
    if (outputDirectory === path.parse(outputDirectory).root)
        throw new Error("outputDirectory cannot be a filesystem root");
    if (requestPath && path.dirname(path.resolve(requestPath)).toLowerCase() !== outputDirectory.toLowerCase())
        throw new Error("request.json must be inside outputDirectory");
    const region = raw.region;
    const monitor = raw.monitor;
    if (!region || !monitor) throw new Error("region and monitor are required");
    for (const [name, rect] of [["region", region], ["monitor", monitor]]) {
        integer(rect.x, `${name}.x`);
        integer(rect.y, `${name}.y`);
        integer(rect.width, `${name}.width`, 1, 32768);
        integer(rect.height, `${name}.height`, 1, 32768);
    }
    if (region.width < 32 || region.height < 80)
        throw new Error("Please select at least 32 × 80 pixels for scrolling capture");
    if (region.x < monitor.x || region.y < monitor.y ||
        region.x + region.width > monitor.x + monitor.width ||
        region.y + region.height > monitor.y + monitor.height)
        throw new Error("region must be inside one monitor");
    if (!Number.isFinite(monitor.scaleFactor) || monitor.scaleFactor < 0.5 || monitor.scaleFactor > 4)
        throw new Error("Invalid monitor.scaleFactor");
    if (region.width * region.height > MAX_PIXELS) throw new Error("Selected region is too large");
    const initialAnnotations = validateAnnotations(raw.initialAnnotations ?? [], outputDirectory);
    const annotationsClip = raw.annotationsClip ?? { x: 0, y: 0, width: region.width, height: region.height };
    for (const key of ["x", "y", "width", "height"]) integer(annotationsClip[key], `annotationsClip.${key}`, key === "width" || key === "height" ? 1 : -200_000);
    return {
        schemaVersion: SCHEMA_VERSION,
        sessionId: raw.sessionId,
        outputDirectory,
        region: { x: region.x, y: region.y, width: region.width, height: region.height },
        monitor: { x: monitor.x, y: monitor.y, width: monitor.width, height: monitor.height, scaleFactor: monitor.scaleFactor },
        initialAnnotations,
        annotationsClip: { ...annotationsClip },
        ...(raw.parentProcessId == null ? {} : { parentProcessId: integer(raw.parentProcessId, "parentProcessId", 1, 2147483647) }),
    };
}

export function validateAnnotations(annotations, outputDirectory) {
    if (!Array.isArray(annotations) || annotations.length > 2000) throw new Error("Invalid initialAnnotations");
    const kinds = ["rect", "ellipse", "line", "arrow", "pen", "text", "mosaic"];
    const number = (value, name, minimum = -200_000, maximum = 200_000) => {
        if (!Number.isFinite(value) || value < minimum || value > maximum) throw new Error(`Invalid annotation ${name}`);
        return value;
    };
    return annotations.map((mark) => {
        if (!mark || !kinds.includes(mark.kind)) throw new Error("Unsupported annotation kind");
        const m = { kind: mark.kind };
        for (const key of ["x1", "y1", "x2", "y2"]) m[key] = number(mark[key], key);
        m.strokeWidth = number(mark.strokeWidth ?? 2, "strokeWidth", 0.1, 1000);
        m.fontSize = number(mark.fontSize ?? 18, "fontSize", 1, 1000);
        if (typeof mark.color !== "string" || !/^#(?:[a-f0-9]{6}|[a-f0-9]{8})$/i.test(mark.color))
            throw new Error("Invalid annotation color");
        m.color = mark.color;
        if (mark.kind === "text") {
            if (typeof mark.text !== "string" || mark.text.length > 100_000) throw new Error("Invalid annotation text");
            m.text = mark.text;
        }
        if (mark.kind === "pen") {
            if (!Array.isArray(mark.points) || mark.points.length < 1 || mark.points.length > 100_000)
                throw new Error("Invalid pen points");
            m.points = mark.points.map((p) => ({ x: number(p.x, "point.x"), y: number(p.y, "point.y") }));
        }
        if (mark.kind === "mosaic") {
            const mosaicPath = typeof mark.mosaicPath === "string" ? path.resolve(outputDirectory, mark.mosaicPath) : "";
            if (!mosaicPath ||
                path.dirname(mosaicPath).toLowerCase() !== outputDirectory.toLowerCase() ||
                !/^mark-\d+\.png$/i.test(path.basename(mark.mosaicPath)))
                throw new Error("Mosaic image must be a mark-N.png inside this session directory");
            m.mosaicPath = mosaicPath;
        }
        return m;
    });
}

export function readRequest(requestPath) {
    if (typeof requestPath !== "string" || !path.isAbsolute(requestPath))
        throw new Error("--nook-request requires an absolute request file");
    const stat = fs.statSync(requestPath);
    if (!stat.isFile() || stat.size > 8 * 1024 * 1024) throw new Error("Invalid request file size");
    const raw = JSON.parse(fs.readFileSync(requestPath, "utf8").replace(/^\uFEFF/, ""));
    const request = validateRequest(raw, requestPath);
    const directory = fs.lstatSync(request.outputDirectory);
    if (!directory.isDirectory() || directory.isSymbolicLink()) throw new Error("Invalid session output directory");
    if (fs.existsSync(path.join(request.outputDirectory, "result.json")))
        throw new Error("Session already has a result");
    if (fs.existsSync(path.join(request.outputDirectory, "ready.json")))
        throw new Error("Session has already been started");
    return request;
}

export function validatePng(png) {
    if (!Buffer.isBuffer(png) || png.length < 33 || png.length > MAX_PNG_BYTES ||
        !png.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])) ||
        png.toString("ascii", 12, 16) !== "IHDR")
        throw new Error("Invalid PNG result");
    const width = png.readUInt32BE(16);
    const height = png.readUInt32BE(20);
    if (!width || !height || width > 32768 || height > 32768 || width * height > MAX_PIXELS)
        throw new Error("PNG result exceeds image limits");
    return { width, height };
}

function atomicWrite(directory, name, data) {
    const temp = path.join(directory, `.${name}.${process.pid}.tmp`);
    const final = path.join(directory, name);
    if (fs.existsSync(final)) throw new Error(`Result already exists: ${name}`);
    try {
        const fd = fs.openSync(temp, "wx");
        try { fs.writeFileSync(fd, data); fs.fsyncSync(fd); }
        finally { fs.closeSync(fd); }
        fs.renameSync(temp, final);
    } finally {
        if (fs.existsSync(temp)) fs.unlinkSync(temp);
    }
}

export function writeResult(request, status, options = {}) {
    if (!["ok", "cancelled", "error"].includes(status)) throw new Error("Invalid result status");
    if (fs.existsSync(path.join(request.outputDirectory, "result.json")))
        throw new Error("Session already has a result");
    const result = { schemaVersion: SCHEMA_VERSION, sessionId: request.sessionId, status };
    if (status === "ok") {
        if (!ACTIONS.includes(options.action)) throw new Error("Invalid result action");
        validatePng(options.png);
        atomicWrite(request.outputDirectory, "result.png", options.png);
        result.action = options.action;
        result.imagePath = "result.png";
    }
    if (options.message) result.message = String(options.message).slice(0, 1600);
    atomicWrite(request.outputDirectory, "result.json", `${JSON.stringify(result)}\n`);
    return result;
}

export function writeReady(request) {
    if (fs.existsSync(path.join(request.outputDirectory, "result.json"))) throw new Error("Session already has a result");
    const result = { schemaVersion: SCHEMA_VERSION, sessionId: request.sessionId, ready: true };
    atomicWrite(request.outputDirectory, "ready.json", `${JSON.stringify(result)}\n`);
    return result;
}
