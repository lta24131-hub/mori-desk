import fs from "node:fs";
import path from "node:path";

export const SCHEMA_VERSION = 1;
export const ACTIONS = Object.freeze(["copy", "save", "pin", "collect"]);
export const MAX_PIXELS = 64_000_000;
export const MAX_PNG_BYTES = 256 * 1024 * 1024;
const sessionPattern = /^[a-f0-9]{32}$/i;
const featureNames = ["rectangle", "ellipse", "lines", "pen", "text", "mosaic", "pin", "collect", "scrolling"];

export function validateFeatures(raw) {
    if (raw != null && (typeof raw !== "object" || Array.isArray(raw))) throw new Error("Invalid screenshot features");
    const features = {};
    for (const name of featureNames) {
        const value = raw?.[name];
        if (value !== undefined && typeof value !== "boolean") throw new Error(`Invalid screenshot feature ${name}`);
        features[name] = value !== false;
    }
    return Object.freeze(features);
}

export function allowsAction(request, action) {
    return action === "save" || action === "copy" ||
        (action === "pin" && request.features?.pin !== false) ||
        (action === "collect" && request.features?.collect !== false);
}

export function hasAnnotations(request) {
    return ["rectangle", "ellipse", "lines", "pen", "text", "mosaic"].some(name => request.features?.[name] !== false);
}

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
    const features = validateFeatures(raw.features);
    if (!features.scrolling) throw new Error("Scrolling capture is disabled");
    const initialAnnotations = validateAnnotations(raw.initialAnnotations ?? [], outputDirectory);
    const annotationsClip = raw.annotationsClip ?? { x: 0, y: 0, width: region.width, height: region.height };
    for (const key of ["x", "y", "width", "height"]) integer(annotationsClip[key], `annotationsClip.${key}`, key === "width" || key === "height" ? 1 : -200_000);
    return {
        schemaVersion: SCHEMA_VERSION,
        sessionId: raw.sessionId,
        outputDirectory,
        features,
        region: { x: region.x, y: region.y, width: region.width, height: region.height },
        monitor: { x: monitor.x, y: monitor.y, width: monitor.width, height: monitor.height, scaleFactor: monitor.scaleFactor },
        initialAnnotations,
        transactionalOutputs: raw.transactionalOutputs === true,
        deferredStart: raw.deferredStart === true,
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
            if (mark.textWrapWidth != null) m.textWrapWidth = number(mark.textWrapWidth, "textWrapWidth", 0, 32768);
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
    const stat = fs.lstatSync(requestPath);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 8 * 1024 * 1024) throw new Error("Invalid request file size");
    const raw = JSON.parse(fs.readFileSync(requestPath, "utf8").replace(/^\uFEFF/, ""));
    const request = validateRequest(raw, requestPath);
    const directory = fs.lstatSync(request.outputDirectory);
    if (!directory.isDirectory() || directory.isSymbolicLink()) throw new Error("Invalid session output directory");
    if (fs.existsSync(path.join(request.outputDirectory, "result.json")))
        throw new Error("Session already has a result");
    if (fs.existsSync(path.join(request.outputDirectory, "ready.json")) ||
        fs.existsSync(path.join(request.outputDirectory, "prepared.json")))
        throw new Error("Session has already been started");
    return request;
}

export function readWarmRequest(filename) {
    if (typeof filename !== "string" || !path.isAbsolute(filename)) throw new Error("--nook-warm requires an absolute warm file");
    const stat = fs.lstatSync(filename);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16_384) throw new Error("Invalid warm file");
    const raw = JSON.parse(fs.readFileSync(filename, "utf8").replace(/^\uFEFF/, ""));
    if (!raw || typeof raw !== "object" || Array.isArray(raw) || raw.schemaVersion !== SCHEMA_VERSION ||
        typeof raw.sessionId !== "string" || !sessionPattern.test(raw.sessionId)) throw new Error("Invalid warm identity");
    if (typeof raw.outputDirectory !== "string" || !path.isAbsolute(raw.outputDirectory)) throw new Error("Invalid warm output directory");
    const outputDirectory = path.resolve(raw.outputDirectory);
    if (outputDirectory === path.parse(outputDirectory).root ||
        path.dirname(path.resolve(filename)).toLowerCase() !== outputDirectory.toLowerCase()) throw new Error("Warm file must belong to its session directory");
    const directory = fs.lstatSync(outputDirectory);
    if (!directory.isDirectory() || directory.isSymbolicLink()) throw new Error("Invalid warm session directory");
    for (const name of ["warm.ready.json", "result.json", "ready.json", "prepared.json"])
        if (fs.existsSync(path.join(outputDirectory, name))) throw new Error("Warm session already started");
    return { schemaVersion: SCHEMA_VERSION, sessionId: raw.sessionId, outputDirectory,
        parentProcessId: integer(raw.parentProcessId, "parentProcessId", 1, 2147483647) };
}

export function readWarmActivation(warm) {
    const filename = path.join(warm.outputDirectory, "request.json");
    if (!fs.existsSync(filename)) return null;
    const request = readRequest(filename);
    if (request.sessionId !== warm.sessionId || request.parentProcessId !== warm.parentProcessId ||
        request.outputDirectory.toLowerCase() !== warm.outputDirectory.toLowerCase() || !request.deferredStart)
        throw new Error("Capture request does not belong to this warm session");
    return request;
}

export function writeWarmReady(warm) {
    const ready = { schemaVersion: SCHEMA_VERSION, sessionId: warm.sessionId, warm: true };
    atomicWrite(warm.outputDirectory, "warm.ready.json", JSON.stringify(ready));
    return ready;
}

export function writeStartupTiming(identity, timings) {
    const data = { schemaVersion: SCHEMA_VERSION, sessionId: identity.sessionId };
    for (const name of ["warm", "appReadyMs", "rendererLoadedMs", "activatedMs", "preparedMs", "captureStartMs", "readyMs"])
        if (name === "warm" ? typeof timings[name] === "boolean" : Number.isFinite(timings[name]) && timings[name] >= 0)
            data[name] = timings[name];
    atomicWrite(identity.outputDirectory, "startup-timing.json", JSON.stringify(data));
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

function atomicWrite(directory, name, data, publication) {
    const temp = path.join(directory, `.${name}.${process.pid}.tmp`);
    const final = path.join(directory, name);
    if (fs.existsSync(final)) throw new Error(`Result already exists: ${name}`);
    try {
        const fd = fs.openSync(temp, "wx");
        try {
            fs.writeFileSync(fd, data);
            fs.fsyncSync(fd);
            // Record the new file's identity before rename. A rename may have
            // committed even if an I/O layer subsequently reports failure.
            if (publication) publication.identity = fs.fstatSync(fd, { bigint: true });
        }
        finally { fs.closeSync(fd); }
        fs.renameSync(temp, final);
    } finally {
        if (fs.existsSync(temp)) fs.unlinkSync(temp);
    }
}

function publicationState(filename, publication) {
    try {
        const current = fs.lstatSync(filename, { bigint: true });
        const original = publication?.identity;
        return original && current.isFile() && !current.isSymbolicLink() &&
            current.dev === original.dev && current.ino === original.ino &&
            current.size === original.size && current.mtimeNs === original.mtimeNs ? "owned" : "other";
    } catch (error) {
        // Permission or storage errors are not proof that a manifest is absent.
        return error.code === "ENOENT" ? "absent" : "unknown";
    }
}

export function writeResult(request, status, options = {}) {
    if (!["ok", "cancelled", "error"].includes(status)) throw new Error("Invalid result status");
    if (fs.existsSync(path.join(request.outputDirectory, "result.json")))
        throw new Error("Session already has a result");
    const result = { schemaVersion: SCHEMA_VERSION, sessionId: request.sessionId, status };
    if (status === "ok") {
        if (!ACTIONS.includes(options.action) || !allowsAction(request, options.action)) throw new Error("Invalid or disabled result action");
        validatePng(options.png);
        if (options.exportId) {
            const id = integer(options.exportId, "exportId", 1, 1000);
            const filename = path.join(request.outputDirectory, `export-${id}.json`);
            const stat = fs.lstatSync(filename);
            if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16_384) throw new Error("Invalid export manifest");
            const manifest = JSON.parse(fs.readFileSync(filename, "utf8"));
            if (manifest.schemaVersion !== SCHEMA_VERSION || manifest.status !== "export" || manifest.exportId !== id ||
                manifest.sessionId !== request.sessionId || manifest.action !== options.action || manifest.imagePath !== `export-${id}.png`)
                throw new Error("Export does not belong to this session");
            result.exportId = id;
            result.imagePath = manifest.imagePath;
        } else {
            atomicWrite(request.outputDirectory, "result.png", options.png);
            result.imagePath = "result.png";
        }
        result.action = options.action;
    }
    if (options.message) result.message = String(options.message).slice(0, 1600);
    atomicWrite(request.outputDirectory, "result.json", `${JSON.stringify(result)}\n`);
    return result;
}

// Export is a request, not a terminal result. The editor and Fabric objects stay
// alive until the parent confirms that the chosen output actually succeeded.
export function writeExportRequest(request, exportId, action, png) {
    if (fs.existsSync(path.join(request.outputDirectory, "result.json"))) throw new Error("Session already has a result");
    integer(exportId, "exportId", 1, 1000);
    if (!ACTIONS.includes(action) || !allowsAction(request, action)) throw new Error("Invalid or disabled export action");
    validatePng(png);
    const stem = `export-${exportId}`;
    const value = { schemaVersion: SCHEMA_VERSION, sessionId: request.sessionId, status: "export", exportId, action, imagePath: `${stem}.png` };
    const imagePath = path.join(request.outputDirectory, `${stem}.png`);
    const manifestPath = path.join(request.outputDirectory, `${stem}.json`);
    if (publicationState(manifestPath) !== "absent") throw new Error(`Result already exists or is inaccessible: ${stem}.json`);
    const imagePublication = {}, manifestPublication = {};
    try {
        atomicWrite(request.outputDirectory, `${stem}.png`, png, imagePublication);
        atomicWrite(request.outputDirectory, `${stem}.json`, `${JSON.stringify(value)}\n`, manifestPublication);
        return value;
    } catch (error) {
        const manifest = publicationState(manifestPath, manifestPublication);
        const image = publicationState(imagePath, imagePublication);
        // The parent can consume a published manifest immediately. Treat a
        // verified commit as success even if rename's caller observed an error.
        if (manifest === "owned" && image === "owned") return value;
        // Only this exact newly-created, unpublished PNG can be rolled back.
        // Existing/replaced files and any published manifest are left alone.
        if (manifest === "absent" && image === "owned") {
            try { fs.unlinkSync(imagePath); } catch { /* Preserve the original publication error. */ }
        }
        throw error;
    }
}

export function readExportReply(request, exportId) {
    integer(exportId, "exportId", 1, 1000);
    const filename = path.join(request.outputDirectory, `export-${exportId}.reply.json`);
    if (!fs.existsSync(filename)) return null;
    const stat = fs.lstatSync(filename);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16_384) throw new Error("Invalid export response");
    const value = JSON.parse(fs.readFileSync(filename, "utf8").replace(/^\uFEFF/, ""));
    if (value.schemaVersion !== SCHEMA_VERSION || value.sessionId !== request.sessionId || value.exportId !== exportId || !["ok", "cancelled", "error"].includes(value.status))
        throw new Error("Export response does not belong to this request");
    return { status: value.status, message: String(value.message ?? "").slice(0, 1600) };
}

export function writeReady(request) {
    if (fs.existsSync(path.join(request.outputDirectory, "result.json"))) throw new Error("Session already has a result");
    const result = { schemaVersion: SCHEMA_VERSION, sessionId: request.sessionId, ready: true };
    atomicWrite(request.outputDirectory, "ready.json", `${JSON.stringify(result)}\n`);
    return result;
}

// Preparation means the hidden renderer and Escape handler are usable, not
// that sampling has started. Only the parent may authorize the first frame
// after removing its own screenshot UI from the selected display.
export function writePrepared(request) {
    if (fs.existsSync(path.join(request.outputDirectory, "result.json"))) throw new Error("Session already has a result");
    const result = { schemaVersion: SCHEMA_VERSION, sessionId: request.sessionId, prepared: true };
    atomicWrite(request.outputDirectory, "prepared.json", `${JSON.stringify(result)}\n`);
    return result;
}

export function readStartRequest(request) {
    const filename = path.join(request.outputDirectory, "start.request");
    let stat;
    try { stat = fs.lstatSync(filename); }
    catch (error) { if (error.code === "ENOENT") return false; throw error; }
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 4096) throw new Error("Invalid start request file");
    // Resolve reparse-point redirection as well as explicit symlinks. Opening
    // with NOFOLLOW where supported and comparing identity closes the usual
    // check/open race; bounded reads cannot allocate a growing signal file.
    const canonical = fs.realpathSync.native(filename);
    const expected = path.resolve(filename);
    if (process.platform === "win32" ? canonical.toLowerCase() !== expected.toLowerCase() : canonical !== expected)
        throw new Error("Invalid start request path");
    const fd = fs.openSync(filename, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
    let data;
    try {
        const opened = fs.fstatSync(fd);
        if (!opened.isFile() || opened.dev !== stat.dev || opened.ino !== stat.ino || opened.size > 4096)
            throw new Error("Invalid start request file");
        const buffer = Buffer.alloc(4097);
        let length = 0, count;
        while (length < buffer.length && (count = fs.readSync(fd, buffer, length, buffer.length - length, length)) > 0)
            length += count;
        if (length > 4096) throw new Error("Invalid start request size");
        const current = fs.lstatSync(filename);
        if (!current.isFile() || current.isSymbolicLink() || current.dev !== opened.dev || current.ino !== opened.ino)
            throw new Error("Invalid start request replacement");
        data = buffer.toString("utf8", 0, length);
    } finally { fs.closeSync(fd); }
    let value;
    try { value = JSON.parse(data.replace(/^\uFEFF/, "")); }
    catch { throw new Error("Invalid start request JSON"); }
    if (!value || typeof value !== "object" || Array.isArray(value) ||
        value.schemaVersion !== SCHEMA_VERSION || value.sessionId !== request.sessionId || value.start !== true)
        throw new Error("Invalid start request for this session");
    return true;
}
