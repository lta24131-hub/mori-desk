import test from 'node:test';
import assert from 'node:assert/strict';
import { assetSourceUpdates, isSafeImageSource } from '../src/assets.mjs';
const base = 'https://nook-canvas.local/index.html';
const resource = `https://nook-canvas.local/resource/${'a'.repeat(32)}/${'b'.repeat(32)}`;
const inline = 'data:image/png;base64,aGVsbG8=';
const asset = { id: 'asset:image', typeName: 'asset', type: 'image', props: { src: inline, w: 123, h: 456, name: 'keep' } };
const scene = { document: { store: { [asset.id]: asset } } };

test('only typed inline images and exact same-origin UUID resources are allowed', () => {
  assert.equal(isSafeImageSource(inline, base), true);
  assert.equal(isSafeImageSource(resource, base), true);
  assert.equal(isSafeImageSource('/resource/' + 'a'.repeat(32) + '/' + 'b'.repeat(32), base), true);
  for (const value of ['https://remote.test/image.png', 'file:///C:/secret.png', 'javascript:alert(1)', 'data:text/html;base64,aA==', '/resource/../secret', '/resource/a/b', resource + '?path=c', resource + '#other', 'https://user:pass@nook-canvas.local/resource/a/b', undefined]) assert.equal(isSafeImageSource(value, base), false, String(value));
  assert.equal(isSafeImageSource(inline, base, false), false);
});

test('native asset ACK changes only src, with no dimensions/geometry/history replacement', () => {
  const updates = assetSourceUpdates({ assets: [{ id: asset.id, src: resource }] }, scene, () => asset, base);
  assert.deepEqual(updates, [{ id: asset.id, type: 'image', props: { src: resource } }]);
  assert.equal(asset.props.src, inline); assert.equal(asset.props.w, 123); assert.equal(asset.props.name, 'keep');
});

test('ACK cannot overwrite a newer inline asset or already-normalized resource', () => {
  for (const src of ['data:image/png;base64,d29ybGQ=', resource]) {
    assert.deepEqual(assetSourceUpdates({ assets: [{ id: asset.id, src: resource }] }, scene, () => ({ ...asset, props: { ...asset.props, src } }), base), []);
  }
});

test('unknown/removed/wrong-type or remote ACK assets are ignored', () => {
  const payload = { assets: [{ id: asset.id, src: resource }, { id: 'asset:unknown', src: resource }, { id: asset.id, src: 'https://evil.test/image.png' }] };
  assert.deepEqual(assetSourceUpdates(payload, scene, () => undefined, base), []);
  assert.deepEqual(assetSourceUpdates(payload, scene, () => ({ ...asset, type: 'video' }), base), []);
  assert.deepEqual(assetSourceUpdates({}, scene, () => asset, base), []);
});
