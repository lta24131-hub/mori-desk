const GAP = 28;
const finite = value => typeof value === 'number' && Number.isFinite(value);
const validBounds = value => value && ['x', 'y', 'w', 'h'].every(key => finite(value[key])) && value.w >= 0 && value.h >= 0;

export function intersects(a, b, gap = GAP) {
  return a.x < b.x + b.w + gap && a.x + a.w + gap > b.x && a.y < b.y + b.h + gap && a.y + a.h + gap > b.y;
}

// Insert one compact batch, not a long strip. The newest batch is selected and
// framed by the editor; its placement never covers previous work on this page.
export function planImageBatch(sizes, viewport, obstacles = [], requestedPosition) {
  if (!sizes.length || sizes.some(size => !finite(size.w) || !finite(size.h) || size.w <= 0 || size.h <= 0)) throw new Error('图片尺寸无效。');
  const maxRowWidth = Math.max(640, Math.min(1800, validBounds(viewport) ? viewport.w * 0.85 : 1200));
  const boxes = [];
  let x = 0, y = 0, rowHeight = 0, width = 0;
  for (const size of sizes) {
    const ratio = Math.min(1, 640 / Math.max(size.w, size.h));
    const w = size.w * ratio, h = size.h * ratio;
    if (x && x + w > maxRowWidth) { y += rowHeight + GAP; x = 0; rowHeight = 0; }
    boxes.push({ x, y, w, h });
    width = Math.max(width, x + w); rowHeight = Math.max(rowHeight, h); x += w + GAP;
  }
  const height = y + rowHeight;
  const center = requestedPosition && finite(requestedPosition.x) && finite(requestedPosition.y)
    ? requestedPosition
    : validBounds(viewport) ? { x: viewport.x + viewport.w / 2, y: viewport.y + viewport.h / 2 } : { x: 0, y: 0 };
  const bounds = { x: center.x - width / 2, y: center.y - height / 2, w: width, h: height };
  const existing = obstacles.filter(validBounds);
  if (existing.some(obstacle => intersects(bounds, obstacle))) {
    bounds.y = existing.reduce((bottom, obstacle) => Math.max(bottom, obstacle.y + obstacle.h), -Infinity) + GAP;
  }
  return { boxes: boxes.map(box => ({ ...box, x: box.x + bounds.x, y: box.y + bounds.y })), bounds };
}
