const resourcePath = /^\/resource\/(?:[a-f0-9]{32}|[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})\/(?:[a-f0-9]{32}|[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})$/i;

export function isSafeImageSource(src, baseHref, allowInline = true) {
  if (typeof src !== 'string') return false;
  if (allowInline && /^data:image\/(?:png|jpeg|jpg|webp|gif|bmp|avif|svg\+xml);base64,/i.test(src)) return true;
  try {
    const base = new URL(baseHref);
    const url = new URL(src, base);
    return url.origin === base.origin && !url.username && !url.password && resourcePath.test(url.pathname) && !url.search && !url.hash;
  } catch { return false; }
}

// Apply just src on acknowledged assets. A late ACK may never replace a newer
// edited asset, reload the document, clear undo, or change a shape's geometry.
export function assetSourceUpdates(payload, capturedScene, getAsset, baseHref) {
  const snapshot = capturedScene?.document || capturedScene;
  const candidates = Array.isArray(payload.assets) ? payload.assets : [];
  const result = [];
  for (const candidate of candidates) {
    if (!candidate || typeof candidate.id !== 'string' || !isSafeImageSource(candidate.src, baseHref, false)) continue;
    const savedAsset = snapshot?.store?.[candidate.id];
    const currentAsset = getAsset(candidate.id);
    if (savedAsset?.typeName !== 'asset' || savedAsset.type !== 'image' || currentAsset?.type !== 'image') continue;
    const source = savedAsset.props?.src;
    if (typeof source !== 'string' || !source.startsWith('data:') || currentAsset.props?.src !== source) continue;
    result.push({ id: currentAsset.id, type: 'image', props: { src: candidate.src } });
  }
  return result;
}
