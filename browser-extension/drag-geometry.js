(() => {
  const MARGIN = 12;
  const GAP = 44;
  const WIDTH = 408;
  const HEIGHT = 300;
  const clamp = (value, min, max) => Math.min(Math.max(value, min), Math.max(min, max));

  function place(point, viewport) {
    const width = Math.max(1, Math.min(WIDTH, viewport.width - MARGIN * 2));
    const height = Math.max(1, Math.min(HEIGHT, viewport.height - MARGIN * 2));
    const roomRight = viewport.width - point.x - GAP - MARGIN;
    const roomLeft = point.x - GAP - MARGIN;
    const preferredX = roomRight >= width || roomRight >= roomLeft ? point.x + GAP : point.x - GAP - width;
    return {
      x: clamp(preferredX, MARGIN, viewport.width - width - MARGIN),
      y: clamp(point.y - height * 0.42, MARGIN, viewport.height - height - MARGIN),
      width,
      height
    };
  }

  function fit(rect, viewport, size = {width: WIDTH, height: HEIGHT}) {
    const width = Math.max(1, Math.min(size.width, viewport.width - MARGIN * 2));
    const height = Math.max(1, Math.min(size.height, viewport.height - MARGIN * 2));
    return {x: clamp(rect.x, MARGIN, viewport.width - width - MARGIN), y: clamp(rect.y, MARGIN, viewport.height - height - MARGIN), width, height};
  }

  function distance(point, rect) {
    const dx = Math.max(rect.x - point.x, 0, point.x - rect.x - rect.width);
    const dy = Math.max(rect.y - point.y, 0, point.y - rect.y - rect.height);
    return Math.hypot(dx, dy);
  }

  globalThis.MoriDragGeometry = Object.freeze({place, fit, distance});
})();
