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

  // Ctrl is intentionally not a zoom modifier. Applying the public constrained
  // camera API also avoids altering SDK inputs.ctrlKey (which is delayed by
  // 150ms on release and is shared with shape constraints and selection).
  const delta = {
    x: -(event.shiftKey && !event.altKey ? event.deltaY : event.deltaX),
    y: event.shiftKey && !event.altKey ? 0 : -event.deltaY,
    z: -Math.max(-10, Math.min(10, event.deltaY)) / 100,
  };
  const camera = editor.getCamera();
  const options = editor.getCameraOptions();
  if (event.altKey) {
    if (!delta.z) return true;
    const zoom = camera.z + delta.z * options.zoomSpeed * camera.z;
    const bounds = editor.getViewportScreenBounds();
    const x = event.clientX - bounds.x, y = event.clientY - bounds.y;
    editor.setCamera({ x: camera.x + x / zoom - x / camera.z, y: camera.y + y / zoom - y / camera.z, z: zoom }, { immediate: true });
  } else {
    editor.setCamera({ x: camera.x + delta.x * options.panSpeed / camera.z, y: camera.y + delta.y * options.panSpeed / camera.z, z: camera.z }, { immediate: true });
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
