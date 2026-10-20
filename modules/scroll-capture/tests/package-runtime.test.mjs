import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

test("packaged component advertises deferred-start compatibility without changing its schema", () => {
    const source = fs.readFileSync(new URL("../package-runtime.mjs", import.meta.url), "utf8");
    const start = source.indexOf("const digest = ");
    assert.ok(start >= 0, "execute the production manifest publication block");
    const output = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "synthetic-package");
    const app = path.join(output, "resources/app");
    const bytes = Buffer.from("synthetic bundled host; no runtime is launched");
    const writes = [];
    vm.runInNewContext(source.slice(start), {
        path, output, app, createHash, electronVersion: "44.3.0",
        installed: new Map([["native", { name: "native", version: "1.0.0", license: "MIT" }]]),
        fs: {
            readFileSync(filename) { assert.equal(filename, path.join(app, "out/main/main.js")); return bytes; },
            writeFileSync(filename, value) { writes.push({ filename, value }); },
        },
        console: { log() {} },
    });
    assert.equal(writes.length, 1);
    assert.equal(writes[0].filename, path.join(output, "component.json"));
    const manifest = JSON.parse(writes[0].value);
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(manifest.deferredStart, true, "a new parent must reject a helper missing this explicit capability");
    assert.equal(manifest.captureFeaturePolicy, true, "restricted screenshot features require an explicit helper capability");
    assert.equal(manifest.capturePrewarm, true, "warm invocation requires an explicit helper capability");
    assert.equal(manifest.entryPoint, "Nook.ScrollCapture.exe");
    assert.equal(manifest.applicationSha256, createHash("sha256").update(bytes).digest("hex"));
    assert.equal(manifest.sourceDirectory, "corresponding-source");
});
