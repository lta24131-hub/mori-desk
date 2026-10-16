// Run with ELECTRON_RUN_AS_NODE=1. Does not enumerate/capture monitors, start
// input hooks, create a window, or read the clipboard.
const assert = require('node:assert/strict');
const path = require('node:path');
const { createRequire } = require('node:module');
const app = path.resolve(process.argv[2]);
const runtime = createRequire(path.join(app, 'package.json'));
assert.equal(process.versions.electron, '44.3.0');
const screen = runtime('node-screenshots');
const input = runtime('uiohook-napi');
assert.equal(typeof screen.Monitor, 'function');
assert.equal(typeof screen.Monitor.all, 'function');
assert.equal(typeof input.uIOhook.start, 'function');
assert.equal(typeof input.uIOhook.stop, 'function');
console.log('PASS packaged Electron / screenshot binding / input-hook binding load; no screen or input API invoked');
