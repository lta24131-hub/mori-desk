import { act, cleanup, fireEvent, render, waitFor } from '@testing-library/react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { createShapeId, DefaultDashStyle, DefaultSizeStyle } from 'tldraw';
import { applyCanvasWheel } from '../src/wheel';
import * as thumbnail from '../src/thumbnail';

const sent: { type: string; requestId: string; session?: string; payload?: Record<string, unknown> }[] = [];
const listeners = new Set<(event: MessageEvent) => void>();
let NookCanvas: typeof import('../src/main')['NookCanvas'];
beforeAll(async () => {
  window.chrome = { webview: {
    postMessage(value) { sent.push(value as typeof sent[number]); },
    addEventListener(_type, listener) { listeners.add(listener); },
    removeEventListener(_type, listener) { listeners.delete(listener); },
  } };
  window.history.replaceState({}, '', '/?test=1');
  NookCanvas = (await import('../src/main')).NookCanvas;
});
afterEach(() => { cleanup(); sent.length = 0; listeners.clear(); });
async function mount(payload: Record<string, unknown> = {}) {
  const view = render(<NookCanvas />);
  await waitFor(() => expect(sent.filter(message => message.type === 'ready')).toHaveLength(1));
  const api = window.nookCanvas!;
  await act(async () => { await api.receive({ protocol: 'nook-canvas-v1', type: 'load', session: 'test-session', requestId: 'load-test', payload: { name: 'SDK mount regression', revision: 0, isNewProject: true, ...payload } }); });
  await waitFor(() => expect(sent.filter(message => message.type === 'loaded')).toHaveLength(1));
  return { view, api, editor: api.editor };
}
async function receive(type: string, requestId: string, payload: Record<string, unknown> = {}) {
  await act(async () => { await window.nookCanvas!.receive({ protocol: 'nook-canvas-v1', type, requestId, payload, session: 'test-session' }); });
}
const image = (id: string, w = 400, h = 300) => ({ id, src: 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1kAAAAASUVORK5CYII=', width: w, height: h });

describe('actual mounted SDK', () => {
  it('generates an existing project preview after loading without requiring an edit or extra save', async () => {
    const capture = vi.spyOn(thumbnail, 'captureCanvasThumbnail').mockResolvedValue(image('preview').src);
    try {
      const { editor, api } = await mount();
      await receive('addImages', 'preview-image', { images: [image('preview-image')] });
      const scene = editor.getSnapshot();
      sent.length = 0;
      await act(async () => { await api.receive({ protocol: 'nook-canvas-v1', type: 'load', session: 'preview-project', requestId: 'preview-load', payload: { scene, revision: 7, isNewProject: false } }); });
      await waitFor(() => expect(sent.filter(message => message.type === 'thumbnail')).toHaveLength(1), { timeout: 3500 });
      const preview = sent.find(message => message.type === 'thumbnail')!;
      expect(preview.session).toBe('preview-project');
      expect(preview.payload).toEqual({ revision: 7, dataUrl: image('preview').src });
      expect(sent.filter(message => message.type === 'save')).toHaveLength(0);
      expect(api.state()).toMatchObject({ loaded: true, revision: 7, change: 0, savedChange: 0 });
      expect(editor.getCurrentPageShapes()).toHaveLength(1);
      expect(capture).toHaveBeenCalledTimes(1);
    } finally { capture.mockRestore(); }
  });

  it('imports, saves and flushes using production timer defaults under Chromium receiver rules', async () => {
    const originalSet = globalThis.setTimeout, originalClear = globalThis.clearTimeout;
    const checkReceiver = (receiver: unknown) => {
      if (receiver != null && receiver !== globalThis && receiver !== window) throw new TypeError('Illegal invocation');
    };
    const schedule = vi.spyOn(globalThis, 'setTimeout').mockImplementation(function (this: unknown, callback, delay, ...args) {
      checkReceiver(this);
      return Reflect.apply(originalSet, globalThis, [callback, delay, ...args]);
    });
    const cancel = vi.spyOn(globalThis, 'clearTimeout').mockImplementation(function (this: unknown, id) {
      checkReceiver(this);
      return Reflect.apply(originalClear, globalThis, [id]);
    });
    try {
      const { editor, api } = await mount();
      await receive('flush', 'empty-close');
      const empty = sent.find(message => message.type === 'save' && message.requestId === 'empty-close');
      expect(empty?.payload?.scene).toBeTruthy();
      await receive('saved', 'empty-close', { revision: 1, assets: [] });
      await receive('addImages', 'browser-import', { images: [image('browser-a'), image('browser-b')] });
      expect(editor.getCurrentPageShapes()).toHaveLength(2);
      expect(sent.find(message => message.type === 'imagesAdded' && message.requestId === 'browser-import')?.payload?.count).toBe(2);
      await receive('flush', 'browser-close');
      const close = sent.find(message => message.type === 'save' && message.requestId === 'browser-close');
      expect(close?.payload?.revision).toBe(1);
      await receive('saved', 'browser-close', { revision: 2, assets: [] });
      expect(api.state()).toMatchObject({ loaded: true, revision: 2 });
      expect(sent.filter(message => message.type === 'error')).toHaveLength(0);
      expect(document.querySelector('[role="alert"]')).toBeNull();
      cleanup();
      expect(listeners.size).toBe(0);
    } finally {
      cancel.mockRestore(); schedule.mockRestore();
    }
  });

  it('keeps one editor and one ready handshake across name, empty, dirty, saving, saved, and error renders', async () => {
    const { editor } = await mount();
    await receive('addImages', 'images-1', { images: [image('one'), image('two')] });
    await waitFor(() => expect(editor.getCurrentPageShapes()).toHaveLength(2));
    await receive('flush', 'flush-1');
    const save = sent.find(message => message.type === 'save' && message.requestId === 'flush-1')!;
    expect(save.payload?.scene).toBeTruthy();
    await receive('saved', 'flush-1', { revision: 1, assets: [] });
    await act(async () => { editor.updateShapes([{ id: editor.getSelectedShapeIds()[0], type: 'image', x: 1500 }]); });
    await receive('flush', 'flush-2');
    await receive('error', 'flush-2', { message: 'Fixture disk failure', revision: 1 });
    await waitFor(() => expect(document.querySelector('[role="alert"]')?.textContent).toContain('Fixture disk failure'));
    expect(window.nookCanvas?.editor).toBe(editor);
    expect(editor.getCurrentPageShapes()).toHaveLength(2);
    expect(sent.filter(message => message.type === 'ready')).toHaveLength(1);
    expect(listeners.size).toBe(1);
  });

  it('clears only a recovered save error after a successful durable retry', async () => {
    const { editor } = await mount();
    await receive('addImages', 'retry-image', { images: [image('retry-image')] });
    await receive('flush', 'failed-save');
    await receive('error', 'failed-save', { message: 'Fixture disk full', revision: 0 });
    await waitFor(() => expect(document.querySelector('[role="alert"]')?.textContent).toContain('Fixture disk full'));
    await receive('flush', 'retry-save');
    expect(document.querySelector('[role="alert"]')?.textContent).toContain('Fixture disk full');
    await receive('saved', 'retry-save', { revision: 1, assets: [] });
    await waitFor(() => expect(document.querySelector('[role="alert"]')).toBeNull());
    expect(editor.getCurrentPageShapes()).toHaveLength(1);
    expect(document.querySelector('[role="status"]')?.textContent).toContain('已保存');
  });

  it('retains ordinary import and external-content guidance when a later save succeeds', async () => {
    const { editor } = await mount();
    await receive('addImages', 'empty-import', { images: [] });
    await waitFor(() => expect(document.querySelector('[role="alert"]')?.textContent).toContain('没有可导入的图片'));
    await receive('flush', 'after-import-warning');
    await receive('saved', 'after-import-warning', { revision: 1, assets: [] });
    expect(document.querySelector('[role="alert"]')?.textContent).toContain('没有可导入的图片');
    await act(async () => { await editor.putExternalContent({ type: 'url', url: 'https://example.com/image' }); });
    expect(document.querySelector('[role="alert"]')?.textContent).toContain('请先将网页图片收藏到素材库');
    await receive('flush', 'after-content-warning');
    await receive('saved', 'after-content-warning', { revision: 2, assets: [] });
    expect(document.querySelector('[role="alert"]')?.textContent).toContain('请先将网页图片收藏到素材库');
  });

  it('retains and resolves every existing image beside drawings when a project snapshot is loaded', async () => {
    const { editor, api } = await mount();
    const resources = Array.from({ length: 12 }, (_, index) => ({ ...image(`persisted-${index}`, 100, 80), src: new URL(`/resource/${'a'.repeat(32)}/${(index + 1).toString(16).padStart(32, '0')}`, document.baseURI).href }));
    await receive('addImages', 'persisted-images', { images: resources });
    await act(async () => {
      editor.createShapes(Array.from({ length: 7 }, (_, index) => ({ id: createShapeId(`persisted-draw-${index}`), type: 'draw' as const, x: index * 20, y: 0, props: { segments: [{ type: 'free' as const, points: [{ x: 0, y: 0, z: .5 }, { x: 12, y: 12, z: .5 }] }], isComplete: true } })));
    });
    const snapshot = editor.getSnapshot();
    await act(async () => { await api.receive({ protocol: 'nook-canvas-v1', type: 'load', session: 'persisted-project', requestId: 'persisted-load', payload: { scene: snapshot, revision: 7, isNewProject: false } }); });
    const images = editor.getCurrentPageShapes().filter(shape => shape.type === 'image');
    expect(images).toHaveLength(12);
    expect(editor.getCurrentPageShapes().filter(shape => shape.type === 'draw')).toHaveLength(7);
    for (const shape of images) {
      if (shape.type !== 'image') throw new Error('Expected a retained image');
      const resolved = await editor.resolveAssetUrl(shape.props.assetId, { shouldResolveToOriginal: true });
      expect(resources.some(resource => resource.src === resolved)).toBe(true);
    }
    expect(editor.store.props.assets.resolve).toBeTruthy();
    expect(sent.some(message => message.type === 'error')).toBe(false);
  });

  it('imports distinct compact batches without overlaps, selects and frames the new batch, retries never duplicate, undo removes one batch', async () => {
    const { editor } = await mount();
    const first = { images: [image('first-a'), image('first-b')] };
    await receive('addImages', 'first-command', first);
    const firstIds = [...editor.getSelectedShapeIds()];
    const firstBounds = editor.getSelectionPageBounds()!;
    await receive('addImages', 'first-command', first);
    expect(editor.getCurrentPageShapes()).toHaveLength(2);
    await receive('addImages', 'second-command', { images: [image('second-a'), image('second-b')] });
    const secondIds = editor.getSelectedShapeIds();
    const secondBounds = editor.getSelectionPageBounds()!;
    expect(secondIds).toHaveLength(2);
    expect(secondIds.some(id => firstIds.includes(id))).toBe(false);
    expect(secondBounds.y).toBeGreaterThanOrEqual(firstBounds.maxY + 28);
    expect(editor.getViewportPageBounds().contains(secondBounds)).toBe(true);
    expect(sent.filter(message => message.type === 'imagesAdded').map(message => message.payload?.count)).toEqual([2, 2, 2]);
    await act(async () => { editor.undo(); });
    expect(editor.getCurrentPageShapes().map(shape => shape.id).sort()).toEqual(firstIds.sort());
  });

  it('syncs SDK focus-mode tools visibility both ways without replacing the project', async () => {
    const { editor } = await mount();
    await receive('addImages', 'tools-image', { images: [image('tools-image')] });
    await receive('toolVisibility', 'hide', { visible: false });
    await waitFor(() => expect(editor.getInstanceState().isFocusMode).toBe(true));
    await waitFor(() => expect(sent.filter(message => message.type === 'toolVisibilityChanged').at(-1)?.payload?.visible).toBe(false));
    await act(async () => { editor.updateInstanceState({ isFocusMode: false }); });
    await waitFor(() => expect(sent.filter(message => message.type === 'toolVisibilityChanged').at(-1)?.payload?.visible).toBe(true));
    expect(editor.getCurrentPageShapes()).toHaveLength(1);
    expect(window.nookCanvas?.editor).toBe(editor);
  });

  it('shows opacity presets plus native slider, applies mixed selection opacity and supports undo', async () => {
    const { editor, view } = await mount();
    await receive('addImages', 'opacity-images', { images: [image('opacity-a'), image('opacity-b')] });
    await waitFor(() => expect(view.getByTestId('nook.opacity.0.5')).toBeTruthy());
    expect(view.getByTestId('style.opacity')).toBeTruthy();
    fireEvent.click(view.getByTestId('nook.opacity.0.5'));
    expect(editor.getSelectedShapes().map(shape => shape.opacity)).toEqual([0.5, 0.5]);
    expect(view.getByTestId('nook.opacity.0.5').getAttribute('aria-pressed')).toBe('true');
    await act(async () => { editor.undo(); });
    expect(editor.getSelectedShapes().map(shape => shape.opacity)).toEqual([1, 1]);
  });

  it('exposes six alignment and two distribution actions only for meaningful selections, applies and undoes them', async () => {
    const { editor, view } = await mount();
    expect(view.queryByTestId('style-panel.align-left')).toBeNull();
    await receive('addImages', 'align-images', { images: [image('align-a', 200, 100), image('align-b', 200, 100), image('align-c', 200, 100)] });
    await waitFor(() => expect(view.getByTestId('style-panel.align-left')).toBeTruthy());
    for (const id of ['left', 'right', 'top', 'bottom', 'center-horizontal', 'center-vertical']) expect(view.getByTestId(`style-panel.align-${id}`)).toBeTruthy();
    for (const id of ['horizontal', 'vertical']) expect(view.getByTestId(`style-panel.distribute-${id}`)).toBeTruthy();
    const before = editor.getSelectedShapes().map(shape => ({ id: shape.id, x: shape.x, y: shape.y }));
    fireEvent.click(view.getByTestId('style-panel.align-left'));
    expect(new Set(editor.getSelectedShapes().map(shape => shape.x)).size).toBe(1);
    await act(async () => { editor.undo(); });
    expect(editor.getSelectedShapes().map(shape => ({ id: shape.id, x: shape.x, y: shape.y }))).toEqual(before);
    const ids = editor.getSelectedShapeIds();
    await act(async () => { editor.updateShapes([{ id: ids[1], type: 'image', x: -900 }]); });
    fireEvent.click(view.getByTestId('style-panel.distribute-horizontal'));
    const centers = editor.getSelectedShapes().map(shape => editor.getShapePageBounds(shape)!.midX).sort((a, b) => a - b);
    expect(centers[1] - centers[0]).toBeCloseTo(centers[2] - centers[1], 5);
    await act(async () => { editor.select(ids[0]); });
    await waitFor(() => expect(view.queryByTestId('style-panel.align-left')).toBeNull());
  });

  it('Alt wheel zooms around the cursor while Ctrl/normal wheel only pan and readonly blocks camera input', async () => {
    const { editor } = await mount();
    await act(async () => { editor.focus(); editor.setCamera({ x: 0, y: 0, z: 1 }); });
    const point = { x: 350, y: 250 };
    const beforePoint = editor.screenToPage(point);
    await act(async () => { applyCanvasWheel(editor, new WheelEvent('wheel', { altKey: true, deltaY: -80, clientX: point.x, clientY: point.y, cancelable: true })); });
    expect(editor.getCamera().z).toBeGreaterThan(1);
    expect(editor.screenToPage(point).x).toBeCloseTo(beforePoint.x, 5);
    expect(editor.screenToPage(point).y).toBeCloseTo(beforePoint.y, 5);
    const zoomed = editor.getCamera();
    await act(async () => { applyCanvasWheel(editor, new WheelEvent('wheel', { ctrlKey: true, deltaY: 70, cancelable: true })); });
    expect(editor.getCamera().z).toBe(zoomed.z); expect(editor.getCamera().y).not.toBe(zoomed.y);
    const panned = editor.getCamera();
    await act(async () => { editor.updateInstanceState({ isReadonly: true }); applyCanvasWheel(editor, new WheelEvent('wheel', { altKey: true, deltaY: -50, cancelable: true })); });
    expect(editor.getCamera()).toEqual(panned);
  });

  it('alignment ignores locked shapes instead of using them as a hidden layout anchor', async () => {
    const { editor, view } = await mount();
    await receive('addImages', 'locked-align', { images: [image('locked-a', 100, 100), image('locked-b', 100, 100), image('locked-c', 100, 100)] });
    const ids = [...editor.getSelectedShapeIds()];
    await act(async () => { editor.updateShapes([{ id: ids[2], type: 'image', x: -3000, isLocked: true }]); editor.select(...ids); });
    const unlockedLeft = Math.min(editor.getShape(ids[0])!.x, editor.getShape(ids[1])!.x);
    fireEvent.click(view.getByTestId('style-panel.align-left'));
    expect(editor.getShape(ids[0])!.x).toBe(unlockedLeft); expect(editor.getShape(ids[1])!.x).toBe(unlockedLeft);
    expect(editor.getShape(ids[2])!.x).toBe(-3000);
  });

  it('applies new-project defaults but retains existing project grid/tools/styles on later loads', async () => {
    const { editor, api } = await mount({ canvasPreferences: { gridDefault: true, toolsVisibleDefault: false, snapMode: true, zoomSpeed: 1.6, defaultDash: 'dotted', defaultSize: 'xl', saveDelay: 2200 } });
    expect(editor.getInstanceState().isGridMode).toBe(true); expect(editor.getInstanceState().isFocusMode).toBe(true);
    expect(editor.user.getIsSnapMode()).toBe(true); expect(editor.getCameraOptions().zoomSpeed).toBe(1.6);
    const id = createShapeId('existing-styled-shape');
    await act(async () => { editor.createShape({ id, type: 'geo', props: { geo: 'rectangle', dash: 'dashed', size: 's' } }); editor.updateInstanceState({ isGridMode: false, isFocusMode: false }); });
    const scene = editor.getSnapshot();
    await act(async () => { await api.receive({ protocol: 'nook-canvas-v1', type: 'load', session: 'existing-session', requestId: 'existing-load', payload: { scene, revision: 5, isNewProject: false, canvasPreferences: { gridDefault: true, toolsVisibleDefault: false, snapMode: false, zoomSpeed: 20, defaultDash: 'dotted', defaultSize: 'xl' } } }); });
    expect(editor.getInstanceState().isGridMode).toBe(false); expect(editor.getInstanceState().isFocusMode).toBe(false);
    expect(editor.user.getIsSnapMode()).toBe(false); expect(editor.getCameraOptions().zoomSpeed).toBe(2);
    expect(editor.getStyleForNextShape(DefaultDashStyle)).toBe('dotted'); expect(editor.getStyleForNextShape(DefaultSizeStyle)).toBe('xl');
    const shape = editor.getShape(id)!;
    expect(shape.type === 'geo' && shape.props.dash).toBe('dashed'); expect(shape.type === 'geo' && shape.props.size).toBe('s');
    expect(window.nookCanvas?.editor).toBe(editor);
  });
});
