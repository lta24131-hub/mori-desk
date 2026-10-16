import test from 'node:test';
import assert from 'node:assert/strict';
import { CanvasThumbnailQueue } from '../src/thumbnail-queue.mjs';

function fixture(overrides = {}) {
  const timers = new Map(), published = []; let id = 0, captured = 0;
  const queue = new CanvasThumbnailQueue({
    timer: (action, delay) => { timers.set(++id, { action, delay }); return id; }, clear: key => timers.delete(key),
    capture: async () => { captured++; return 'data:image/png;base64,fixture'; },
    isCurrent: () => true, publish: value => published.push(value), ...overrides,
  });
  return { queue, timers, published, captured: () => captured, fire: async () => { const jobs = [...timers.values()]; timers.clear(); for (const job of jobs) await job.action(); } };
}
test('only latest acknowledged revision produces one debounced preview', async () => {
  const f = fixture(); f.queue.schedule({ revision: 1 }); f.queue.schedule({ revision: 2 });
  assert.equal(f.timers.size, 1); assert.equal([...f.timers.values()][0].delay, 1000);
  await f.fire(); assert.equal(f.captured(), 1); assert.equal(f.published[0].revision, 2);
});
test('unsaved edits or project switches cancel stale preview before and after rendering', async () => {
  let current = false;
  const f = fixture({ isCurrent: () => current }); f.queue.schedule({ revision: 1 }); await f.fire(); assert.equal(f.captured(), 0);
  let finish;
  const g = fixture({ capture: () => new Promise(resolve => { finish = resolve; }), isCurrent: () => current });
  current = true; g.queue.schedule({ revision: 1 }); const running = g.fire(); await Promise.resolve(); current = false; finish('stale'); await running;
  assert.equal(g.published.length, 0);
});
test('empty page clears thumbnail; errors and disposal never claim success', async () => {
  const f = fixture({ capture: async () => '' }); f.queue.schedule({ revision: 3 }); await f.fire(); assert.deepEqual(f.published, [{ revision: 3, dataUrl: '' }]);
  const failed = fixture({ capture: async () => { throw new Error('decode failure'); } }); failed.queue.schedule({ revision: 1 }); await failed.fire(); assert.equal(failed.published.length, 0);
  const disposed = fixture(); disposed.queue.schedule({ revision: 1 }); disposed.queue.dispose(); await disposed.fire(); assert.equal(disposed.captured(), 0);
});
