import test from 'node:test';
import assert from 'node:assert/strict';
import { CanvasBridge, PROTOCOL, parseMessage } from '../src/bridge.mjs';

const message = (type, payload = {}, session = 'session-a', requestId = `host_${type}`) => ({ protocol: PROTOCOL, type, payload, session, requestId });
const turn = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
function setup(overrides = {}) {
  const sent = [], statuses = [], timers = new Map();
  let nextTimer = 1;
  const bridge = new CanvasBridge({
    send: value => { assert.equal(typeof value, 'object'); sent.push(value); },
    capture: () => ({ document: { store: {} }, session: { version: 0 } }),
    apply: async () => {}, commit: async () => {}, normalize: () => {}, insert: async () => {}, setTheme: () => {},
    status: (...value) => statuses.push(value),
    timer: (callback, delay) => { const id = nextTimer++; timers.set(id, { callback, delay }); return id; },
    clear: id => timers.delete(id), ...overrides,
  });
  const fire = async delay => {
    for (const [id, value] of [...timers]) if (value.delay === delay) { timers.delete(id); value.callback(); }
    await turn();
  };
  return { bridge, sent, statuses, timers, fire, saves: () => sent.filter(value => value.type === 'save') };
}
async function load(fixture, session = 'session-a', revision = 0) {
  await fixture.bridge.receive(message('load', { revision }, session));
}
async function ack(fixture, save = fixture.saves().at(-1), assets = []) {
  return fixture.bridge.receive({ ...save, type: 'saved', payload: { revision: save.payload.revision + 1, assets } });
}

test('protocol accepts objects/JSON but rejects unrelated or malformed messages', () => {
  assert.equal(parseMessage('not json'), null);
  assert.equal(parseMessage([]), null);
  assert.equal(parseMessage({ protocol: 'wrong', type: 'load' }), null);
  assert.deepEqual(parseMessage(JSON.stringify(message('theme'))), message('theme'));
});

test('ready is an object without a session; editing and saving are disabled until loaded', async () => {
  const f = setup(); f.bridge.ready(); f.bridge.changed(); await f.bridge.save();
  assert.equal(f.sent[0].type, 'ready'); assert.equal(f.sent[0].session, undefined);
  assert.equal(f.saves().length, 0); assert.equal(f.bridge.change, 0);
  await load(f);
  const loaded = f.sent.find(value => value.type === 'loaded');
  assert.equal(loaded.session, 'session-a'); assert.equal(loaded.requestId, 'host_load'); assert.equal(loaded.payload.revision, 0);
});

test('changes debounce 800ms and report dirty once, not at every pointer event', async () => {
  const f = setup(); await load(f);
  f.bridge.changed(); f.bridge.changed(); f.bridge.changed();
  assert.equal([...f.timers.values()].filter(timer => timer.delay === 800).length, 1);
  assert.equal(f.sent.filter(value => value.type === 'dirty').length, 1);
  await f.fire(800);
  assert.equal(f.saves().length, 1); assert.equal(f.saves()[0].payload.revision, 0);
  await ack(f); assert.equal(f.bridge.revision, 1); assert.equal(f.bridge.savedChange, 3);
  f.bridge.changed(); assert.equal(f.sent.filter(value => value.type === 'dirty').length, 2);
});

test('save uses expected host revision, with no optimistic increment', async () => {
  const f = setup(); await load(f, 'session-a', 7);
  f.bridge.changed(); await f.bridge.save('first');
  assert.equal(f.saves()[0].payload.revision, 7); assert.equal(f.bridge.revision, 7);
  assert.equal(await f.bridge.receive(message('saved', { revision: 7 }, 'session-a', 'first')), false);
  assert.equal(f.bridge.savedChange, 0);
  await ack(f); assert.equal(f.bridge.revision, 8);
  f.bridge.changed(); await f.bridge.save('second'); assert.equal(f.saves()[1].payload.revision, 8);
});

test('new edits during an in-flight save remain dirty and the next save waits for ACK', async () => {
  const f = setup(); await load(f); f.bridge.changed(); await f.bridge.save('first');
  f.bridge.changed(); await f.bridge.save('second');
  assert.equal(f.saves().length, 1); assert.equal(f.bridge.pending.size, 1);
  await ack(f, f.saves()[0]); await turn();
  assert.equal(f.saves().length, 2); assert.equal(f.saves()[1].payload.revision, 1);
  assert.equal(f.bridge.savedChange, 1); assert.equal(f.bridge.change, 2);
  await ack(f); assert.equal(f.bridge.savedChange, 2); assert.equal(f.bridge.revision, 2);
});

test('flush commits text before capture, always saves, and echoes native requestId', async () => {
  const calls = [];
  const f = setup({ commit: async () => { calls.push('commit'); }, capture: () => { calls.push('capture'); return {}; } });
  await load(f); await f.bridge.receive(message('flush', {}, 'session-a', 'flush_native'));
  assert.deepEqual(calls, ['commit', 'capture']);
  assert.equal(f.saves()[0].requestId, 'flush_native'); assert.equal(f.saves()[0].payload.revision, 0);
  await ack(f); assert.equal(f.bridge.revision, 1);
});

test('flush arriving during autosave queues and uses newly acknowledged revision', async () => {
  const calls = [];
  const f = setup({ commit: async () => { calls.push('commit'); } });
  await load(f); f.bridge.changed(); await f.bridge.save('auto');
  await f.bridge.receive(message('flush', {}, 'session-a', 'flush_close'));
  await f.bridge.receive(message('flush', {}, 'session-a', 'flush_close'));
  assert.equal(f.saves().length, 1); assert.equal(f.bridge.forcedSaves.length, 1);
  await ack(f); await turn();
  assert.equal(f.saves().length, 2); assert.equal(f.saves()[1].requestId, 'flush_close');
  assert.equal(f.saves()[1].payload.revision, 1); assert.deepEqual(calls, ['commit']);
});

test('saved normalizes just assets against the captured scene and suppresses sync listeners', async () => {
  const scene = { document: { store: { original: true } } }; let seen;
  const f = setup({ capture: () => scene, normalize: (payload, captured) => { seen = { payload, captured }; f.bridge.changed(); } });
  await load(f); f.bridge.changed(); await f.bridge.save('save');
  const assets = [{ id: 'asset:image', src: 'https://nook-canvas.local/resource/id/id' }];
  await ack(f, f.saves()[0], assets);
  assert.equal(seen.captured, scene); assert.deepEqual(seen.payload.assets, assets);
  assert.equal(f.bridge.change, 1); assert.equal(f.bridge.savedChange, 1);
});

test('native payload.images import completes and reports imagesAdded, then becomes dirty', async () => {
  let imported;
  const f = setup({ insert: async payload => { imported = payload; } }); await load(f);
  const payload = { images: [{ id: 'image', src: 'local', width: 10, height: 10 }] };
  await f.bridge.receive(message('addImages', payload, 'session-a', 'import_native'));
  assert.equal(imported, payload); assert.equal(f.bridge.change, 1);
  assert.equal(f.sent.at(-1).type, 'imagesAdded'); assert.equal(f.sent.at(-1).requestId, 'import_native');
  assert.equal(f.sent.at(-1).payload.count, 1);
});

test('same import command is inserted exactly once, including concurrent retries, and ACK count remains stable', async () => {
  let count = 0, finish;
  const f = setup({ insert: async () => { count++; await new Promise(resolve => { finish = resolve; }); return { count: 2 }; } }); await load(f);
  const command = message('addImages', { images: [{}, {}] }, 'session-a', 'repeat-import');
  const a = f.bridge.receive(command); const b = f.bridge.receive(command); await turn();
  assert.equal(count, 1); assert.equal(f.sent.some(value => value.type === 'imagesAdded'), false);
  finish(); await Promise.all([a, b]); await f.bridge.receive(command);
  assert.equal(count, 1); assert.equal(f.bridge.change, 1);
  assert.deepEqual(f.sent.filter(value => value.type === 'imagesAdded').map(value => value.payload.count), [2, 2, 2]);
});

test('failed import retry is permitted, absent requestId rejected, and no imagesAdded is invented', async () => {
  let count = 0;
  const f = setup({ insert: async () => { if (!count++) throw new Error('bad file'); return { count: 1 }; } }); await load(f);
  assert.equal(await f.bridge.receive({ ...message('addImages'), requestId: '' }), false);
  const command = message('addImages', { images: [{}] }); await f.bridge.receive(command);
  assert.equal(f.sent.some(value => value.type === 'imagesAdded'), false);
  await f.bridge.receive(command); assert.equal(count, 2); assert.equal(f.bridge.change, 1);
});

test('load applies configured save delay safely and tools visibility is explicit/current-session only', async () => {
  const visibility = [], payloads = [];
  const f = setup({ apply: async payload => payloads.push(payload), setToolVisibility: visible => visibility.push(visible) });
  await f.bridge.receive(message('load', { canvasPreferences: { saveDelay: 2200 } })); f.bridge.changed();
  assert.equal([...f.timers.values()][0].delay, 2200); assert.equal(payloads[0].canvasPreferences.saveDelay, 2200);
  assert.equal(await f.bridge.receive(message('toolVisibility', { visible: false })), true);
  assert.equal(await f.bridge.receive(message('toolVisibility', { visible: 'false' })), false);
  assert.equal(await f.bridge.receive(message('toolVisibility', { visible: true }, 'old-session')), false);
  assert.deepEqual(visibility, [false]);
  await f.bridge.receive(message('load', { canvasPreferences: { saveDelay: 90000 } }, 'session-b')); assert.equal(f.bridge.saveDelayMs, 10000);
});

test('derived thumbnails are scheduled only for successful durable ACKs and cannot break save success', async () => {
  const tokens = [];
  const f = setup({ onSaved: token => { tokens.push(token); throw new Error('preview is optional'); } }); await load(f);
  assert.equal(tokens.length, 0); f.bridge.changed(); await f.bridge.save('save');
  await f.bridge.receive(message('saved', { revision: 8 }, 'session-a', 'save')); assert.equal(tokens.length, 0);
  await ack(f); assert.deepEqual(tokens, [{ revision: 1, generation: 1, change: 1 }]); assert.equal(f.bridge.savedChange, 1);
});

test('new session cancels timers and ignores old ACKs, theme and imports', async () => {
  const f = setup(); await load(f); f.bridge.changed(); await f.bridge.save('old_save');
  const old = f.saves()[0]; await load(f, 'session-b');
  assert.equal(f.timers.size, 0); assert.equal(await ack(f, old), false);
  assert.equal(await f.bridge.receive(message('theme', { theme: 'dark' })), false);
  assert.equal(await f.bridge.receive(message('addImages', { images: [] })), false);
  assert.equal(f.bridge.revision, 0); assert.equal(f.bridge.change, 0);
});

test('native interaction freezes only the matching session and requires an explicit boolean', async () => {
  const values = [];
  const f = setup({ setInteraction: enabled => values.push(enabled) }); await load(f);
  assert.equal(await f.bridge.receive(message('interaction', { enabled: false })), true);
  assert.equal(await f.bridge.receive(message('interaction', { enabled: true })), true);
  assert.equal(await f.bridge.receive(message('interaction', { enabled: false }, 'session-old')), false);
  assert.equal(await f.bridge.receive(message('interaction', { enabled: 'false' })), false);
  assert.deepEqual(values, [false, true]);
});

test('same-session load cannot replace edits, including a duplicate during loading', async () => {
  let finish; let count = 0;
  const f = setup({ apply: async () => { count++; await new Promise(resolve => { finish = resolve; }); } });
  const loading = f.bridge.receive(message('load')); await turn();
  assert.equal(await f.bridge.receive(message('load')), false); finish(); await loading;
  f.bridge.changed(); assert.equal(await f.bridge.receive(message('load')), false);
  assert.equal(count, 1); assert.equal(f.bridge.change, 1);
});

test('concurrent session loads serialize application and stale load never announces loaded', async () => {
  let finishA; const applied = [];
  const f = setup({ apply: async payload => { applied.push(payload.name); if (payload.name === 'a') await new Promise(resolve => { finishA = resolve; }); } });
  const a = f.bridge.receive(message('load', { name: 'a' })); await turn();
  const b = f.bridge.receive(message('load', { name: 'b', revision: 4 }, 'session-b')); await turn();
  assert.deepEqual(applied, ['a']); assert.equal(f.bridge.loaded, false);
  finishA(); await Promise.all([a, b]);
  assert.deepEqual(applied, ['a', 'b']); assert.equal(f.bridge.session, 'session-b'); assert.equal(f.bridge.revision, 4);
  assert.deepEqual(f.sent.filter(value => value.type === 'loaded').map(value => value.session), ['session-b']);
});

test('load failure sends error, disables saving, and does not claim a loaded canvas', async () => {
  const f = setup({ apply: async () => { throw new Error('bad snapshot'); } });
  await load(f); assert.equal(f.bridge.loaded, false); assert.equal(f.bridge.loading, false);
  assert.equal(f.sent.at(-1).type, 'error'); assert.equal(f.sent.some(value => value.type === 'loaded'), false);
  f.bridge.changed(); await f.bridge.save(); assert.equal(f.saves().length, 0);
});

test('failed commit or capture never reports save success or clears dirty edits', async () => {
  const f = setup({ commit: async () => { throw new Error('text not committed'); } });
  await load(f); f.bridge.changed(); await f.bridge.save('flush_failed', true);
  assert.equal(f.saves().length, 0); assert.equal(f.bridge.savedChange, 0); assert.equal(f.bridge.change, 1);
  assert.equal(f.sent.at(-1).requestId, 'flush_failed'); assert.equal(f.sent.at(-1).type, 'error');
});

test('save error retains dirty data, fails queued flushes and permits explicit retry', async () => {
  const f = setup(); await load(f); f.bridge.changed(); await f.bridge.save('failed');
  await f.bridge.save('flush_waiting', true);
  await f.bridge.receive(message('error', { revision: 0, message: 'disk full' }, 'session-a', 'failed'));
  assert.equal(f.bridge.change, 1); assert.equal(f.bridge.savedChange, 0); assert.equal(f.bridge.pending.size, 0);
  assert.equal(f.sent.at(-1).type, 'error'); assert.equal(f.sent.at(-1).requestId, 'flush_waiting');
  await f.bridge.save('retry', true); assert.equal(f.saves().at(-1).payload.revision, 0);
  await ack(f); assert.equal(f.bridge.savedChange, 1);
});

test('missing ACK keeps edits and exposes retry instead of silently losing a close request', async () => {
  const f = setup(); await load(f); f.bridge.changed(); await f.bridge.save('slow');
  await f.bridge.save('flush_close', true); await f.fire(10000);
  assert.equal(f.bridge.savedChange, 0); assert.equal(f.bridge.change, 1); assert.equal(f.bridge.pending.size, 0);
  assert.equal(f.statuses.at(-1)[0], 'error'); assert.equal(f.sent.at(-1).requestId, 'flush_close');
});

test('host error can resynchronize expected revision for retry after a late ACK', async () => {
  const f = setup(); await load(f); f.bridge.changed(); await f.bridge.save('old'); await f.fire(10000);
  await f.bridge.save('retry'); await f.bridge.receive(message('error', { revision: 1, message: 'stale revision' }, 'session-a', 'retry'));
  await f.bridge.save('resynced'); assert.equal(f.saves().at(-1).payload.revision, 1);
  await ack(f); assert.equal(f.bridge.revision, 2);
});

test('dispose cancels autosave/ACK timers and delayed messages cannot save', async () => {
  const f = setup(); await load(f); f.bridge.changed(); await f.bridge.save('pending');
  f.bridge.dispose(); assert.equal(f.timers.size, 0); await f.fire(800); await f.fire(10000);
  assert.equal(await ack(f, f.saves()[0]), false); assert.equal(f.saves().length, 1);
});
