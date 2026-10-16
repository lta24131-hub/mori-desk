// Match the durable native asset store. SVG / AVIF may be added through the
// native picker (which rasterizes them); never stage unsupported raw data URIs.
export const SUPPORTED_IMAGE_MIME_TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif', 'image/bmp'];

export function availableActions(actions) {
  const result = { ...actions };
  for (const key of Object.keys(result)) {
    if (key.startsWith('export-') || ['insert-embed', 'print', 'open-cursor-chat'].includes(key)) delete result[key];
  }
  return result;
}

// A native transition freezes physical input, but host imports and saved asset
// ACKs still need to change the store. Keep the unlock entirely synchronous.
export function withWritableEditor(editor, action) {
  const readonly = editor.getInstanceState().isReadonly;
  if (readonly) editor.updateInstanceState({ isReadonly: false });
  try { return action(); }
  finally { if (readonly) editor.updateInstanceState({ isReadonly: true }); }
}
