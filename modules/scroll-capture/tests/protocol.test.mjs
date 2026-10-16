import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
    ACTIONS,
    MAX_PIXELS,
    readRequest,
    validateAnnotations,
    validatePng,
    validateRequest,
    writeReady,
    writeResult,
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
