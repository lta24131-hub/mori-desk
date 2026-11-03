import type { Editor } from 'tldraw';

// Change only the wheel gesture. Native Space/middle-button panning, drawing
// modifiers and touch pinch gestures keep their mature SDK behavior.
export function applyCanvasWheel(editor: Editor, event: WheelEvent) {
  if (!editor.getIsFocused()) return false;
  const editing = editor.getEditingShape();
  if (editing && editor.getShapeUtil(editing).canScroll(editing)) return false;
  event.preventDefault();
  event.stopImmediatePropagation();
  if (editor.getInstanceState().isReadonly || editor.getIsMenuOpen() || editor.getCameraOptions().isLocked) return true;

  // Ordinary wheel zooms immediately; Shift keeps horizontal navigation.
  // Applying the public camera API avoids changing SDK modifier state, which
  // is also used by drawing constraints and selection.
  const panHorizontally = event.shiftKey && !event.altKey && !event.ctrlKey;
  const delta = {
    x: -(panHorizontally ? event.deltaY : event.deltaX),
    z: -Math.max(-10, Math.min(10, event.deltaY)) / 100,
  };
  const camera = editor.getCamera();
  const options = editor.getCameraOptions();
  if (!panHorizontally && event.deltaY !== 0) {
    if (!delta.z) return true;
    const baseZoom = editor.getBaseZoom();
    const zoom = Math.max(options.zoomSteps[0] * baseZoom, Math.min(options.zoomSteps[options.zoomSteps.length - 1] * baseZoom, camera.z + delta.z * options.zoomSpeed * camera.z));
    if (zoom === camera.z) return true;
    const bounds = editor.getViewportScreenBounds();
    const x = event.clientX - bounds.x, y = event.clientY - bounds.y;
    editor.setCamera({ x: camera.x + x / zoom - x / camera.z, y: camera.y + y / zoom - y / camera.z, z: zoom }, { immediate: true });
  } else {
    editor.setCamera({ x: camera.x + delta.x * options.panSpeed / camera.z, y: camera.y, z: camera.z }, { immediate: true });
  }
  return true;
}

export function installCanvasWheel(editor: Editor) {
  const container = editor.getContainer();
  const onWheel = (event: WheelEvent) => {
    const target = event.target;
    // Leave toolbar popovers, menus and style sliders to their native scrolling.
    if (!(target instanceof Element) || !target.closest('.tl-canvas')) return;
    if (target.closest('textarea, input, [contenteditable="true"]')) return;
    applyCanvasWheel(editor, event);
  };
  container.addEventListener('wheel', onWheel, { capture: true, passive: false });
  return () => container.removeEventListener('wheel', onWheel, true);
}
