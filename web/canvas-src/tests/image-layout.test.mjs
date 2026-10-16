import test from 'node:test';
import assert from 'node:assert/strict';
import { planImageBatch, intersects } from '../src/image-layout.mjs';

const viewport = { x: -500, y: -400, w: 1000, h: 800 };
test('batch uses compact rows, bounded image size and stable aspect ratios', () => {
  const result = planImageBatch(Array.from({ length: 8 }, () => ({ w: 2400, h: 1200 })), viewport);
  assert.equal(result.boxes.length, 8);
  assert.ok(result.bounds.w <= 850);
  assert.ok(new Set(result.boxes.map(box => box.y)).size > 1);
  for (const box of result.boxes) { assert.equal(box.w, 640); assert.equal(box.h, 320); }
  for (const [i, box] of result.boxes.entries()) for (const other of result.boxes.slice(i + 1)) assert.equal(intersects(box, other, 0), false);
});
test('existing work including locked/group bounds never gets covered by a new batch', () => {
  const obstacles = [{ x: -200, y: -200, w: 400, h: 400 }, { x: -900, y: 900, w: 40, h: 60 }];
  const result = planImageBatch([{ w: 400, h: 300 }, { w: 300, h: 400 }], viewport, obstacles);
  assert.equal(result.bounds.y, 988);
  assert.equal(obstacles.some(obstacle => intersects(result.bounds, obstacle, 0)), false);
});
test('empty space at explicit drop position is retained and invalid positions are ignored', () => {
  const sizes = [{ w: 20, h: 10 }];
  assert.deepEqual(planImageBatch(sizes, viewport, [], { x: 2000, y: 1000 }).bounds, { x: 1990, y: 995, w: 20, h: 10 });
  assert.deepEqual(planImageBatch(sizes, viewport, [null, { x: NaN }], { x: Infinity, y: 0 }).bounds, { x: -10, y: -5, w: 20, h: 10 });
  assert.throws(() => planImageBatch([{ w: 0, h: 10 }], viewport));
});

