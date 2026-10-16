import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { availableActions, SUPPORTED_IMAGE_MIME_TYPES, withWritableEditor } from '../src/policies.mjs';

test('native unsupported download/print/embed actions are absent, full local editing is retained', () => {
  const ids = ['export-as-png', 'export-all-as-svg', 'insert-embed', 'print', 'open-cursor-chat', 'edit-link', 'group', 'undo', 'copy-as-png', 'insert-media'];
  const actions = Object.fromEntries(ids.map(id => [id, { id, onSelect: () => {} }]));
  const retained = availableActions(actions);
  assert.deepEqual(Object.keys(retained), ['edit-link', 'group', 'undo', 'copy-as-png', 'insert-media']);
  assert.equal(actions['export-as-png'].id, 'export-as-png');
});

test('inline upload only accepts formats supported by the durable native store', () => {
  assert.deepEqual(SUPPORTED_IMAGE_MIME_TYPES, ['image/png', 'image/jpeg', 'image/webp', 'image/gif', 'image/bmp']);
  assert.equal(SUPPORTED_IMAGE_MIME_TYPES.includes('image/svg+xml'), false);
  assert.equal(SUPPORTED_IMAGE_MIME_TYPES.includes('image/avif'), false);
});

test('WebView adapter posts an object, stages no document in browser persistence, and bounds background paint wait', async () => {
  const source = await readFile(new URL('../src/main.tsx', import.meta.url), 'utf8');
  assert.match(source, /native\.postMessage\(hostMessage\)/);
  assert.doesNotMatch(source, /native\.postMessage\(JSON\.stringify/);
  assert.doesNotMatch(source, /persistenceKey=/);
  assert.match(source, /setTimeout\(finish, 100\)/);
  assert.match(source, /acceptedImageMimeTypes=\{SUPPORTED_IMAGE_MIME_TYPES\}/);
});

test('host operations can mutate a frozen editor and restore its freeze even on failure', () => {
  for (const initialReadonly of [false, true]) {
    let readonly = initialReadonly;
    const changes = [];
    const editor = { getInstanceState: () => ({ isReadonly: readonly }), updateInstanceState: state => { readonly = state.isReadonly; changes.push(readonly); } };
    assert.equal(withWritableEditor(editor, () => { assert.equal(readonly, false); return 'saved'; }), 'saved');
    assert.equal(readonly, initialReadonly);
    assert.throws(() => withWritableEditor(editor, () => { assert.equal(readonly, false); throw new Error('failed'); }), /failed/);
    assert.equal(readonly, initialReadonly);
    assert.deepEqual(changes, initialReadonly ? [false, true, false, true] : []);
  }
});
