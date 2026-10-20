import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
    ACTIONS,
    MAX_PIXELS,
    readRequest,
    readWarmRequest, readWarmActivation, writeWarmReady, writeStartupTiming,
    validateAnnotations,
    validatePng,
    validateRequest,
    writeReady,
    writeResult,
    writeExportRequest,
    readExportReply,
    writePrepared,
    readStartRequest,
    validateFeatures,
    allowsAction,
    hasAnnotations,
} from "../host/protocol.mjs";

// All fixtures are generated inside a fresh temporary directory. No display,
// Electron process, user asset, network request, or real library is involved.
const onePixelPng = Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aavsAAAAASUVORK5CYII=",
    "base64",
);
const sessionId = "cafe0123456789abcdef0123456789ab";

function fixture(t) {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nook-scroll-protocol-test-"));
    t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const requestPath = path.join(directory, "request.json");
    const raw = {
        schemaVersion: 1,
        sessionId,
        outputDirectory: directory,
        region: { x: -1800, y: -1050, width: 800, height: 1000 },
        monitor: { x: -1920, y: -1080, width: 1920, height: 1080, scaleFactor: 1.5 },
    };
    return {
        directory,
        requestPath,
        raw,
        request: () => validateRequest(raw, requestPath),
        saveRequest: (value = raw) => fs.writeFileSync(requestPath, JSON.stringify(value)),
    };
}

test("warm bootstrap requires an exact directory, session and mandatory live-parent identity", (t) => {
    const f = fixture(t);
    const filename = path.join(f.directory, "warm.json");
    const raw = { schemaVersion: 1, sessionId, outputDirectory: f.directory, parentProcessId: 1234 };
    for (const replacement of [{ schemaVersion: 2 }, { sessionId: "invalid" }, { parentProcessId: undefined }, { parentProcessId: 0 }, { parentProcessId: "1234" }, { outputDirectory: os.tmpdir() }]) {
        fs.writeFileSync(filename, JSON.stringify({ ...raw, ...replacement }));
        assert.throws(() => readWarmRequest(filename));
    }
    fs.writeFileSync(filename, JSON.stringify(raw));
    const warm = readWarmRequest(filename);
    assert.deepEqual(warm, raw);
    assert.equal(readWarmActivation(warm), null);
    writeWarmReady(warm);
    assert.throws(() => readWarmRequest(filename), /already started/);
    for (const replacement of [{ parentProcessId: 1235 }, { sessionId: "dead0123456789abcdef0123456789ab" }, { deferredStart: false }, { parentProcessId: undefined }]) {
        f.saveRequest({ ...f.raw, parentProcessId: 1234, deferredStart: true, ...replacement });
        assert.throws(() => readWarmActivation(warm), /does not belong/);
    }
    f.saveRequest({ ...f.raw, parentProcessId: 1234, deferredStart: true, features: { pin: false } });
    assert.equal(readWarmActivation(warm).features.pin, false);
});

test("warm timing diagnostics allow only phase numbers, never source paths or pixel payloads", (t) => {
    const f = fixture(t);
    writeStartupTiming(f.request(), { warm: true, appReadyMs: 13, readyMs: 99, rendererLoadedMs: -1, activatedMs: NaN, imagePath: f.directory, png: "private pixels", sourceUrl: "private source" });
    const data = JSON.parse(fs.readFileSync(path.join(f.directory, "startup-timing.json"), "utf8"));
    assert.deepEqual(data, { schemaVersion: 1, sessionId, warm: true, appReadyMs: 13, readyMs: 99 });
    assert.throws(() => writeStartupTiming(f.request(), {}), /already exists/);
    assert.equal(fs.readdirSync(f.directory).some(name => name.endsWith(".tmp")), false);
});

test("physical region retains negative monitor coordinates and mixed-DPI scale", (t) => {
    const f = fixture(t);
    const request = f.request();
    assert.deepEqual(request.region, f.raw.region);
    assert.deepEqual(request.monitor, f.raw.monitor);
    assert.deepEqual(request.initialAnnotations, []);
    assert.deepEqual(request.annotationsClip, { x: 0, y: 0, width: 800, height: 1000 });
    assert.equal(request.outputDirectory, path.resolve(f.directory));
    assert.notEqual(request.region, f.raw.region, "validation must not share mutable geometry");
});

test("feature policy defaults remain compatible and explicit values must be booleans", (t) => {
    const f = fixture(t);
    assert.ok(Object.values(f.request().features).every(value => value === true));
    const raw = { pin: false, collect: false, rectangle: false };
    const policy = validateFeatures(raw);
    raw.pin = true;
    assert.equal(policy.pin, false, "feature policy is an immutable request snapshot");
    assert.equal(policy.text, true);
    assert.equal(Object.isFrozen(policy), true);
    for (const bad of [[], "false", 0, { pin: "false" }, { collect: 0 }, { text: null }])
        assert.throws(() => validateFeatures(bad), /Invalid screenshot feature/);
    assert.throws(() => validateRequest({ ...f.raw, features: { scrolling: false } }, f.requestPath), /disabled/);
});

test("disabled output destinations fail before creating export or result files", (t) => {
    const f = fixture(t);
    const features = Object.fromEntries(["rectangle", "ellipse", "lines", "pen", "text", "mosaic", "pin", "collect"].map(name => [name, false]));
    const request = validateRequest({ ...f.raw, features }, f.requestPath);
    assert.equal(hasAnnotations(request), false);
    for (const action of ["pin", "collect"]) {
        assert.equal(allowsAction(request, action), false);
        assert.throws(() => writeExportRequest(request, 1, action, pngNeverRead()), /disabled/);
        assert.throws(() => writeResult(request, "ok", { action, png: pngNeverRead() }), /disabled/);
    }
    assert.equal(fs.readdirSync(f.directory).length, 0);
    for (const action of ["save", "copy"]) assert.equal(allowsAction(request, action), true);
    assert.equal(allowsAction(request, "unknown"), false);
    function pngNeverRead() { return undefined; }
});

test("request rejects wrong session/schema, sibling outputs, and unsafe geometry", (t) => {
    const f = fixture(t);
    const invalid = [
        { schemaVersion: 2 },
        { sessionId: "../old-session" },
        { sessionId: sessionId.slice(1) },
        { outputDirectory: path.parse(f.directory).root },
        { outputDirectory: "relative/session" },
        { outputDirectory: `${f.directory}-sibling` },
        { region: { ...f.raw.region, x: -1921 } },
        { region: { ...f.raw.region, y: -1081 } },
        { region: { ...f.raw.region, width: 2000 } },
        { region: { ...f.raw.region, height: 79 } },
        { region: { ...f.raw.region, width: 31 } },
        { region: { ...f.raw.region, x: 0.5 } },
        { monitor: { ...f.raw.monitor, scaleFactor: Number.NaN } },
        { monitor: { ...f.raw.monitor, scaleFactor: 0 } },
        { monitor: { ...f.raw.monitor, scaleFactor: 4.01 } },
    ];
    for (const change of invalid) {
        assert.throws(() => validateRequest({ ...f.raw, ...change }, f.requestPath), undefined,
            `accepted invalid request: ${JSON.stringify(change)}`);
    }
    assert.throws(() => validateRequest({
        ...f.raw,
        region: { x: 0, y: 0, width: 8001, height: 8000 },
        monitor: { x: 0, y: 0, width: 9000, height: 9000, scaleFactor: 1 },
    }, f.requestPath), /too large/);
    assert.equal(MAX_PIXELS, 64_000_000);
});

test("request files support BOM but never reopen a terminal session", (t) => {
    const f = fixture(t);
    fs.writeFileSync(f.requestPath, `\uFEFF${JSON.stringify(f.raw)}`);
    assert.equal(readRequest(f.requestPath).sessionId, sessionId);
    writeResult(f.request(), "cancelled");
    assert.throws(() => readRequest(f.requestPath), /already has a result/);
    assert.throws(() => readRequest("relative/request.json"), /absolute/);
});

test("readiness is session-bound, atomic, and cannot reopen an old session", (t) => {
    const f = fixture(t);
    f.saveRequest();
    const request = f.request();
    const ready = writeReady(request);
    assert.deepEqual(ready, { schemaVersion: 1, sessionId, ready: true });
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(f.directory, "ready.json"), "utf8")), ready);
    assert.throws(() => writeReady(request), /already exists/);
    assert.throws(() => readRequest(f.requestPath), /already been started/);
    writeResult(request, "cancelled");
    assert.throws(() => writeReady(request), /already has a result/);
    assert.equal(fs.readdirSync(f.directory).some((name) => name.endsWith(".tmp")), false);
});

test("parent-process identity is optional but never a string, zero, or unsafe PID", (t) => {
    const f = fixture(t);
    assert.equal(Object.hasOwn(f.request(), "parentProcessId"), false);
    assert.equal(validateRequest({ ...f.raw, parentProcessId: 4242 }, f.requestPath).parentProcessId, 4242);
    for (const parentProcessId of [0, -1, 1.5, "4242", 2147483648, NaN]) {
        assert.throws(() => validateRequest({ ...f.raw, parentProcessId }, f.requestPath), /parentProcessId/);
    }
});

test("deferred startup is explicitly opted in and preparation never claims capture readiness", (t) => {
    const f = fixture(t);
    for (const value of [undefined, false, "true", 1, {}, null])
        assert.equal(validateRequest({ ...f.raw, deferredStart: value }, f.requestPath).deferredStart, false);
    const request = validateRequest({ ...f.raw, deferredStart: true }, f.requestPath);
    assert.equal(request.deferredStart, true);
    f.saveRequest();
    const prepared = writePrepared(request);
    assert.deepEqual(prepared, { schemaVersion: 1, sessionId, prepared: true });
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(f.directory, "prepared.json"), "utf8")), prepared);
    assert.equal(fs.existsSync(path.join(f.directory, "ready.json")), false);
    assert.throws(() => writePrepared(request), /exists/);
    assert.throws(() => readRequest(f.requestPath), /already been started/);
    writeResult(request, "cancelled");
    assert.throws(() => writePrepared(request), /already has a result/);
    assert.equal(fs.readdirSync(f.directory).some(name => name.endsWith(".tmp")), false);
});

test("start requests require an explicit session-bound signal and reject invalid or oversized input", (t) => {
    const f = fixture(t), request = f.request();
    const filename = path.join(f.directory, "start.request");
    const valid = { schemaVersion: 1, sessionId, start: true };
    assert.equal(readStartRequest(request), false);
    for (const change of [{ schemaVersion: 2 }, { sessionId: "0".repeat(32) }, { start: false }, { start: "true" }, { start: 1 }]) {
        fs.writeFileSync(filename, JSON.stringify({ ...valid, ...change }));
        assert.throws(() => readStartRequest(request), /start request/i);
    }
    for (const data of ["", "{", "null", "[]", "x".repeat(4097)]) {
        fs.writeFileSync(filename, data);
        assert.throws(() => readStartRequest(request), /start request/i);
    }
    fs.writeFileSync(filename, `\uFEFF${JSON.stringify(valid)}`);
    assert.equal(readStartRequest(request), true);
    assert.equal(fs.readFileSync(filename, "utf8"), `\uFEFF${JSON.stringify(valid)}`, "the parent owns its signal file");
});

test("start signals reject non-files and symbolic links without reading their targets", (t) => {
    const f = fixture(t), filename = path.join(f.directory, "start.request");
    fs.mkdirSync(filename);
    assert.throws(() => readStartRequest(f.request()), /start request/i);
    fs.rmdirSync(filename);
    fs.writeFileSync(filename, JSON.stringify({ schemaVersion: 1, sessionId, start: true }));
    const lstat = fs.lstatSync, read = fs.readFileSync, open = fs.openSync;
    t.mock.method(fs, "lstatSync", (name, ...args) => {
        const stat = lstat(name, ...args);
        if (name === filename) stat.isSymbolicLink = () => true;
        return stat;
    });
    t.mock.method(fs, "readFileSync", (name, ...args) => {
        assert.notEqual(name, filename, "a linked start file must not be read");
        return read(name, ...args);
    });
    t.mock.method(fs, "openSync", (name, ...args) => {
        assert.notEqual(name, filename, "a linked start file must not be opened");
        return open(name, ...args);
    });
    assert.throws(() => readStartRequest(f.request()), /start request/i);
});

test("start signals reject filesystem redirection and a file replaced between validation and open", (t) => {
    const f = fixture(t), filename = path.join(f.directory, "start.request");
    const payload = JSON.stringify({ schemaVersion: 1, sessionId, start: true });
    fs.writeFileSync(filename, payload);
    const canonical = fs.realpathSync.native;
    const redirect = t.mock.method(fs.realpathSync, "native", (name, ...args) =>
        name === filename ? path.join(f.directory, "redirected.request") : canonical(name, ...args));
    assert.throws(() => readStartRequest(f.request()), /start request path/i);
    redirect.mock.restore();

    const replacement = path.join(f.directory, "replacement.request");
    fs.writeFileSync(replacement, payload);
    const open = fs.openSync;
    t.mock.method(fs, "openSync", (name, ...args) => {
        if (name === filename) fs.renameSync(replacement, filename);
        return open(name, ...args);
    });
    assert.throws(() => readStartRequest(f.request()), /start request file/i);
    assert.equal(fs.readFileSync(filename, "utf8"), payload, "invalid handshakes never delete another writer's signal");
});

test("a start signal growing after open is bounded and closes its file handle on rejection", (t) => {
    const f = fixture(t), filename = path.join(f.directory, "start.request");
    fs.writeFileSync(filename, JSON.stringify({ schemaVersion: 1, sessionId, start: true }));
    const read = fs.readSync, close = fs.closeSync;
    let grew = false, closed = 0, startDescriptor;
    t.mock.method(fs, "readSync", (fd, buffer, offset, length, position) => {
        startDescriptor = fd;
        assert.ok(buffer.length <= 4097);
        if (!grew) { grew = true; fs.appendFileSync(filename, " ".repeat(8192)); }
        return read(fd, buffer, offset, length, position);
    });
    t.mock.method(fs, "closeSync", fd => { if (fd === startDescriptor) closed++; return close(fd); });
    assert.throws(() => readStartRequest(f.request()), /start request size/i);
    assert.equal(closed, 1, "the start descriptor is closed even when bounded reading rejects the file");
});

test("each supported success action publishes PNG before terminal JSON", (t) => {
    for (const action of ACTIONS) {
        const f = fixture(t);
        const result = writeResult(f.request(), "ok", { action, png: onePixelPng });
        const disk = JSON.parse(fs.readFileSync(path.join(f.directory, "result.json"), "utf8"));
        assert.deepEqual(result, {
            schemaVersion: 1, sessionId, status: "ok", action, imagePath: "result.png",
        });
        assert.deepEqual(disk, result);
        assert.deepEqual(fs.readFileSync(path.join(f.directory, disk.imagePath)), onePixelPng);
        assert.deepEqual(fs.readdirSync(f.directory).sort(), ["result.json", "result.png"]);
    }
});

test("invalid action or PNG cannot leave a success marker or image", (t) => {
    const f = fixture(t);
    const request = f.request();
    assert.throws(() => writeResult(request, "ok", { action: "upload", png: onePixelPng }), /action/);
    assert.throws(() => writeResult(request, "ok", { action: "save", png: Buffer.from("not png") }), /PNG/);
    assert.deepEqual(fs.readdirSync(f.directory), []);
    assert.throws(() => writeResult(request, "finished"), /status/);
    assert.deepEqual(fs.readdirSync(f.directory), []);
});

test("one cancellation is terminal: late completion must not leave an orphan image", (t) => {
    const f = fixture(t);
    const request = f.request();
    const cancelled = writeResult(request, "cancelled", { action: "save", png: onePixelPng });
    assert.deepEqual(cancelled, { schemaVersion: 1, sessionId, status: "cancelled" });
    const before = fs.readFileSync(path.join(f.directory, "result.json"));
    assert.throws(() => writeResult(request, "ok", { action: "save", png: onePixelPng }));
    assert.throws(() => writeResult(request, "cancelled"));
    assert.deepEqual(fs.readFileSync(path.join(f.directory, "result.json")), before);
    assert.deepEqual(fs.readdirSync(f.directory), ["result.json"]);
});

test("error never promises a downloadable image and bounds diagnostic length", (t) => {
    const f = fixture(t);
    const result = writeResult(f.request(), "error", {
        action: "collect", png: onePixelPng, message: "x".repeat(3000),
    });
    assert.equal(result.status, "error");
    assert.equal(result.message.length, 1600);
    assert.equal(Object.hasOwn(result, "action"), false);
    assert.equal(Object.hasOwn(result, "imagePath"), false);
    assert.deepEqual(fs.readdirSync(f.directory), ["result.json"]);
});

test("PNG limits are checked before output allocation", () => {
    assert.deepEqual(validatePng(onePixelPng), { width: 1, height: 1 });
    for (const [width, height] of [[0, 1], [1, 0], [32769, 1], [8001, 8000]]) {
        const png = Buffer.from(onePixelPng);
        png.writeUInt32BE(width, 16);
        png.writeUInt32BE(height, 20);
        assert.throws(() => validatePng(png), /limits/);
    }
    const malformed = Buffer.from(onePixelPng);
    malformed.write("TEXT", 12, "ascii");
    assert.throws(() => validatePng(malformed), /PNG/);
});

test("all seven existing annotation kinds migrate without external file access", (t) => {
    const f = fixture(t);
    const base = { x1: 10, y1: 20, x2: 50, y2: 60, color: "#80AA33FF", strokeWidth: 3, fontSize: 20 };
    const marks = ["rect", "ellipse", "line", "arrow"].map((kind) => ({ kind, ...base }));
    marks.push({ kind: "text", ...base, text: "small text\n中文" });
    marks.push({ kind: "pen", ...base, points: [{ x: 0.5, y: 1.5 }, { x: 10, y: 20 }] });
    marks.push({ kind: "mosaic", ...base, mosaicPath: "mark-6.png" });
    const validated = validateAnnotations(marks, f.directory);
    assert.deepEqual(validated.slice(0, 6), marks.slice(0, 6));
    assert.equal(validated[6].mosaicPath, path.join(f.directory, "mark-6.png"));
    assert.deepEqual(fs.readdirSync(f.directory), [], "validation must not create or read annotation images");
});

test("annotation payload rejects external mosaic paths and malformed editable data", (t) => {
    const f = fixture(t);
    const base = { kind: "mosaic", x1: 0, y1: 0, x2: 100, y2: 100, color: "#112233" };
    for (const mosaicPath of ["../mark-1.png", "sub/mark-1.png", "result.png", path.join(path.dirname(f.directory), "mark-1.png")]) {
        assert.throws(() => validateAnnotations([{ ...base, mosaicPath }], f.directory), /Mosaic/);
    }
    assert.throws(() => validateAnnotations([{ ...base, kind: "image" }], f.directory), /kind/);
    assert.throws(() => validateAnnotations([{ ...base, color: "url(https://invalid)" }], f.directory), /color/);
    assert.throws(() => validateAnnotations([{ ...base, kind: "text", text: null }], f.directory), /text/);
    assert.throws(() => validateAnnotations([{ ...base, kind: "pen", points: [] }], f.directory), /points/);
    assert.throws(() => validateAnnotations([{ ...base, kind: "line", x1: Infinity }], f.directory), /x1/);
});

test("text wrapping is optional for legacy marks and bounded for editable migration", (t) => {
    const f = fixture(t);
    const mark = { kind: "text", x1: 0, y1: 0, x2: 180, y2: 80, color: "#112233", text: "中文 English" };
    assert.equal(Object.hasOwn(validateAnnotations([mark], f.directory)[0], "textWrapWidth"), false);
    assert.equal(validateAnnotations([{ ...mark, textWrapWidth: 180 }], f.directory)[0].textWrapWidth, 180);
    for (const textWrapWidth of [-1, Infinity, NaN, "180", 32769])
        assert.throws(() => validateAnnotations([{ ...mark, textWrapWidth }], f.directory), /textWrapWidth/);
});

test("transactional outputs are opt-in and export requests are non-terminal", (t) => {
    const f = fixture(t);
    assert.equal(f.request().transactionalOutputs, false);
    assert.equal(validateRequest({ ...f.raw, transactionalOutputs: true }, f.requestPath).transactionalOutputs, true);
    assert.equal(validateRequest({ ...f.raw, transactionalOutputs: "true" }, f.requestPath).transactionalOutputs, false);
    const output = writeExportRequest(f.request(), 1, "save", onePixelPng);
    assert.deepEqual(output, { schemaVersion: 1, sessionId, status: "export", exportId: 1, action: "save", imagePath: "export-1.png" });
    assert.deepEqual(fs.readdirSync(f.directory).sort(), ["export-1.json", "export-1.png"]);
    assert.deepEqual(fs.readFileSync(path.join(f.directory, output.imagePath)), onePixelPng);
    assert.throws(() => writeExportRequest(f.request(), 1, "save", onePixelPng), /exists/);
    assert.equal(fs.existsSync(path.join(f.directory, "result.json")), false);
});

test("a failed manifest publication rolls back only its unpublished PNG and allows the same export ID to retry", (t) => {
    const f = fixture(t), request = f.request();
    const manifest = path.join(f.directory, "export-1.json");
    const rename = fs.renameSync;
    let fail = true;
    t.mock.method(fs, "renameSync", (from, to) => {
        if (to === manifest && fail) { fail = false; throw Object.assign(new Error("transient manifest publication failure"), { code: "EIO" }); }
        return rename(from, to);
    });
    assert.throws(() => writeExportRequest(request, 1, "save", onePixelPng), /transient manifest/);
    assert.deepEqual(fs.readdirSync(f.directory), [], "a retryable failure leaves neither an orphan PNG nor a partial manifest");
    const output = writeExportRequest(request, 1, "copy", onePixelPng);
    assert.equal(output.action, "copy");
    assert.deepEqual(fs.readFileSync(path.join(f.directory, output.imagePath)), onePixelPng);
    assert.deepEqual(JSON.parse(fs.readFileSync(manifest, "utf8")), output);
});

test("ambiguous rename failures distinguish an unpublished PNG from a committed export request", (t) => {
    const rename = fs.renameSync;
    let failingTarget;
    t.mock.method(fs, "renameSync", (from, to) => {
        const result = rename(from, to);
        if (to === failingTarget) { failingTarget = undefined; throw Object.assign(new Error("rename completed before transport error"), { code: "EIO" }); }
        return result;
    });
    const unpublished = fixture(t), request = unpublished.request();
    failingTarget = path.join(unpublished.directory, "export-1.png");
    assert.throws(() => writeExportRequest(request, 1, "save", onePixelPng), /rename completed/);
    assert.deepEqual(fs.readdirSync(unpublished.directory), [], "a moved PNG without a manifest can safely roll back");
    assert.equal(writeExportRequest(request, 1, "save", onePixelPng).exportId, 1);

    const committed = fixture(t);
    failingTarget = path.join(committed.directory, "export-1.json");
    const output = writeExportRequest(committed.request(), 1, "save", onePixelPng);
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(committed.directory, "export-1.json"), "utf8")), output);
    assert.deepEqual(fs.readFileSync(path.join(committed.directory, "export-1.png")), onePixelPng);
    assert.throws(() => writeExportRequest(committed.request(), 1, "copy", onePixelPng), /exists/, "a published request cannot be replaced or retried as another action");
});

test("export publication never removes or overwrites pre-existing same-name files", (t) => {
    for (const extension of ["png", "json"]) {
        const f = fixture(t);
        const name = `export-1.${extension}`, prior = Buffer.from(`existing ${extension} owned by somebody else`);
        fs.writeFileSync(path.join(f.directory, name), prior);
        assert.throws(() => writeExportRequest(f.request(), 1, "save", onePixelPng), /exists/);
        assert.deepEqual(fs.readFileSync(path.join(f.directory, name)), prior);
        assert.deepEqual(fs.readdirSync(f.directory), [name], "pre-existing manifest is rejected before creating its PNG sibling");
    }
});

test("rollback leaves a PNG replaced after publication alone and never removes a published manifest's PNG", (t) => {
    const rename = fs.renameSync;
    let onManifest;
    t.mock.method(fs, "renameSync", (from, to) => {
        if (to.endsWith("export-1.json")) return onManifest(from, to);
        return rename(from, to);
    });
    const replaced = fixture(t), pngPath = path.join(replaced.directory, "export-1.png");
    const other = Buffer.from("replacement owned by another writer");
    onManifest = () => {
        const replacement = path.join(replaced.directory, "replacement.tmp");
        fs.writeFileSync(replacement, other);
        rename(replacement, pngPath);
        throw new Error("manifest failed after PNG replacement");
    };
    assert.throws(() => writeExportRequest(replaced.request(), 1, "save", onePixelPng), /PNG replacement/);
    assert.deepEqual(fs.readFileSync(pngPath), other);

    const published = fixture(t);
    const foreignManifest = JSON.stringify({ imagePath: "export-1.png", publisher: "another writer" });
    onManifest = (_from, to) => { fs.writeFileSync(to, foreignManifest); throw new Error("manifest appeared concurrently"); };
    assert.throws(() => writeExportRequest(published.request(), 1, "save", onePixelPng), /concurrently/);
    assert.equal(fs.readFileSync(path.join(published.directory, "export-1.json"), "utf8"), foreignManifest);
    assert.deepEqual(fs.readFileSync(path.join(published.directory, "export-1.png")), onePixelPng);
});

test("export replies bind session and request ID and reject malformed or oversized files", (t) => {
    const f = fixture(t);
    const request = f.request();
    const filename = path.join(f.directory, "export-1.reply.json");
    const reply = { schemaVersion: 1, sessionId, exportId: 1, status: "cancelled" };
    assert.equal(readExportReply(request, 1), null);
    for (const status of ["ok", "cancelled", "error"]) {
        fs.writeFileSync(filename, JSON.stringify({ ...reply, status, message: "x".repeat(2000) }));
        assert.deepEqual(readExportReply(request, 1), { status, message: "x".repeat(1600) });
    }
    for (const change of [{ schemaVersion: 2 }, { sessionId: "0".repeat(32) }, { exportId: 2 }, { status: "finished" }]) {
        fs.writeFileSync(filename, JSON.stringify({ ...reply, ...change }));
        assert.throws(() => readExportReply(request, 1), /request/);
    }
    fs.writeFileSync(filename, "x".repeat(16_385));
    assert.throws(() => readExportReply(request, 1), /response/);
    for (const id of [0, -1, 1.5, "1", 1001]) assert.throws(() => readExportReply(request, id), /exportId/);
});

test("confirmed export finalizes the owned image without rewriting it and cannot reopen", (t) => {
    const f = fixture(t);
    const request = f.request();
    writeExportRequest(request, 1, "copy", onePixelPng);
    const pngPath = path.join(f.directory, "export-1.png");
    const before = fs.statSync(pngPath).mtimeMs;
    const result = writeResult(request, "ok", { action: "copy", exportId: 1, png: onePixelPng });
    assert.equal(result.imagePath, "export-1.png");
    assert.equal(result.exportId, 1);
    assert.equal(fs.statSync(pngPath).mtimeMs, before);
    assert.equal(fs.existsSync(path.join(f.directory, "result.png")), false);
    assert.throws(() => writeExportRequest(request, 2, "copy", onePixelPng), /already has a result/);
    assert.equal(fs.existsSync(path.join(f.directory, "export-2.png")), false);
});

test("export completion rejects mismatched manifests and invalid IDs before terminal write", (t) => {
    const f = fixture(t);
    const request = f.request();
    const output = writeExportRequest(request, 1, "save", onePixelPng);
    const filename = path.join(f.directory, "export-1.json");
    for (const change of [{ schemaVersion: 2 }, { status: "ok" }, { exportId: 2 }, { sessionId: "0".repeat(32) }, { action: "copy" }, { imagePath: "../export-1.png" }]) {
        fs.writeFileSync(filename, JSON.stringify({ ...output, ...change }));
        assert.throws(() => writeResult(request, "ok", { action: "save", exportId: 1, png: onePixelPng }), /session/);
        assert.equal(fs.existsSync(path.join(f.directory, "result.json")), false);
    }
    for (const id of [0, -1, 1.5, "1", 1001]) assert.throws(() => writeExportRequest(request, id, "copy", onePixelPng), /exportId/);
});
