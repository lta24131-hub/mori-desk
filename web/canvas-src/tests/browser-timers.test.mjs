import test from 'node:test';
import assert from 'node:assert/strict';
import { CanvasBridge, PROTOCOL } from '../src/bridge.mjs';
import { CanvasThumbnailQueue } from '../src/thumbnail-queue.mjs';

// Node and JSDOM accept arbitrary receivers for their timer shims. Chromium's
// Window timers do not: extracting one onto a class and calling this.timer()
// throws "Illegal invocation". Exercise the DEFAULT production scheduler, not
// the injected arrow functions used by the deterministic protocol fixtures.
function browserTimers(t) {
  const originalSet = globalThis.setTimeout, originalClear = globalThis.clearTimeout;
  const tasks = new Map();
  let nextId = 0;
  function checkReceiver(receiver) {
    if (receiver != null && receiver !== globalThis) throw new TypeError('Illegal invocation');
  }
  globalThis.setTimeout = function (callback, delay) {
    checkReceiver(this);
    const id = ++nextId;
    tasks.set(id, { callback, delay });
    return id;
  };
  globalThis.clearTimeout = function (id) { checkReceiver(this); tasks.delete(id); };
  t.after(() => { globalThis.setTimeout = originalSet; globalThis.clearTimeout = originalClear; });
  return {
    tasks,
    async fire(delay) {
      for (const [id, task] of [...tasks]) {
        if (task.delay !== delay) continue;
        tasks.delete(id);
        await task.callback();
      }
      for (let i = 0; i < 8; i++) await Promise.resolve();
    },
  };
}

const message = (type, requestId, payload = {}) => ({ protocol: PROTOCOL, session: 'browser-session', type, requestId, payload });
function bridge(options = {}) {
  const sent = [], statuses = [];
  const value = new CanvasBridge({
    send: event => sent.push(event),
    capture: () => ({ document: { store: {} } }),
    apply: async () => {}, commit: async () => {}, normalize: () => {},
    insert: async () => ({ count: 2 }), setTheme: () => {},
    status: (...status) => statuses.push(status), ...options,
  });
  return { value, sent, statuses };
}
const saved = (requestId, revision) => message('saved', requestId, { revision, assets: [] });

test('browser timer fixture rejects a class receiver exactly like Window timers', t => {
  browserTimers(t);
  const detached = { timer: globalThis.setTimeout, clear: globalThis.clearTimeout };
  assert.throws(() => detached.timer(() => {}, 1), /Illegal invocation/);
  assert.throws(() => detached.clear(1), /Illegal invocation/);
});

test('default browser timers let an untouched canvas flush and acknowledge closing', async t => {
  const timers = browserTimers(t), f = bridge();
  await f.value.receive(message('load', 'load', { revision: 4 }));
  await f.value.receive(message('flush', 'close-window'));
  const save = f.sent.find(event => event.type === 'save');
  assert.ok(save, 'closing must emit save instead of swallowing Illegal invocation');
  assert.equal(save.requestId, 'close-window');
  assert.equal(save.payload.revision, 4);
  assert.equal([...timers.tasks.values()].filter(task => task.delay === 10000).length, 1);
  await f.value.receive(saved('close-window', 5));
  assert.equal(f.value.revision, 5);
  assert.equal(f.value.pending.size, 0);
  assert.equal(timers.tasks.size, 0);
  assert.equal(f.sent.some(event => event.type === 'error'), false);
  f.value.dispose();
});

test('default browser timers debounce imported edits, cancel timeouts and publish a thumbnail', async t => {
  const timers = browserTimers(t), published = [];
  const previews = new CanvasThumbnailQueue({ capture: async () => 'data:image/png;base64,fixture', isCurrent: () => true, publish: event => published.push(event) });
  const f = bridge({ onSaved: token => previews.schedule(token) });
  await f.value.receive(message('load', 'load', { revision: 0 }));
  await f.value.receive(message('addImages', 'import-two', { images: [{}, {}] }));
  assert.equal(f.sent.find(event => event.type === 'imagesAdded')?.payload.count, 2);
  assert.equal(f.sent.some(event => event.type === 'error'), false);
  f.value.changed();
  assert.equal([...timers.tasks.values()].filter(task => task.delay === 800).length, 1);
  await timers.fire(800);
  const save = f.sent.find(event => event.type === 'save');
  assert.ok(save);
  await f.value.receive(saved(save.requestId, 1));
  await timers.fire(1000);
  assert.deepEqual(published, [{ revision: 1, dataUrl: 'data:image/png;base64,fixture' }]);
  assert.equal(f.value.change, f.value.savedChange);
  assert.equal(f.statuses.some(status => status[0] === 'error'), false);
  previews.schedule({ revision: 1 }); previews.schedule({ revision: 1 });
  previews.dispose(); f.value.dispose();
  assert.equal(timers.tasks.size, 0);
});

test('default browser ACK timeout stays recoverable and dispose cancels both pending timer types', async t => {
  const timers = browserTimers(t), f = bridge();
  await f.value.receive(message('load', 'load'));
  f.value.changed();
  await timers.fire(800);
  await timers.fire(10000);
  assert.equal(f.statuses.at(-1)[0], 'error');
  assert.match(f.statuses.at(-1)[1], /保存尚未确认/);
  assert.equal(f.value.pending.size, 0);
  await f.value.receive(message('flush', 'retry'));
  assert.equal(f.sent.filter(event => event.type === 'save').length, 2);
  f.value.changed();
  assert.equal(timers.tasks.size, 2);
  f.value.dispose();
  assert.equal(timers.tasks.size, 0);
});
