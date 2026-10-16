import React, { useCallback, useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { getAssetUrls } from '@tldraw/assets/selfHosted';
import {
  Tldraw, Editor, createTLUser, atom,
  DefaultMainMenu, DefaultContextMenu, ViewSubmenu, PreferencesGroup,
  TldrawUiMenuGroup, TldrawUiMenuItem, TldrawUiMenuSubmenu, KeyboardShortcutsMenuItem, createShapeId,
  UndoRedoGroup, ClipboardMenuGroup, CopyAsMenuGroup, MiscMenuGroup,
  ToggleLockMenuItem, UnlockAllMenuItem, SelectAllMenuItem,
  ArrangeMenuSubmenu, ReorderMenuSubmenu, MoveToPageMenu, useEditor, useValue,
  getUserPreferences, setUserPreferences,
  type TLUserPreferences, type TLEditorSnapshot, type TLStoreSnapshot,
  type TLImageAsset, type TLAssetId, type TLAssetPartial, type TLUiOverrides, type TLComponents,
} from 'tldraw';
import 'tldraw/tldraw.css';
import './style.css';
import { CanvasBridge, PROTOCOL } from './bridge.mjs';
import { assetSourceUpdates, isSafeImageSource as safeSource } from './assets.mjs';
import { availableActions, SUPPORTED_IMAGE_MIME_TYPES, withWritableEditor } from './policies.mjs';
import { applyCanvasPreferences, type CanvasPreferences } from './preferences';
import { installCanvasWheel } from './wheel';
import { NookStylePanel } from './style-panel';
import { planImageBatch } from './image-layout.mjs';
import { captureCanvasThumbnail } from './thumbnail';
import { CanvasThumbnailQueue } from './thumbnail-queue.mjs';

type HostMessage = { protocol: string; type: string; session?: string; requestId?: string; payload?: Record<string, unknown> };
type Scene = Partial<TLEditorSnapshot> | TLStoreSnapshot;
type HostPayload = { scene?: Scene; revision?: number; theme?: 'light' | 'dark'; name?: string; pinned?: boolean; isNewProject?: boolean; canvasPreferences?: CanvasPreferences };
type HostImage = { id?: string; fileId?: string; src?: string; assetUrl?: string; dataURL?: string; mimeType?: string; name?: string; width?: number; height?: number; w?: number; h?: number; props?: TLImageAsset['props'] };
type ImportPayload = { images?: HostImage[]; files?: HostImage[]; items?: HostImage[]; position?: { x: number; y: number } };
type CanvasMessage = { text: string; source: 'notice' | 'save-error' };
declare global {
  interface Window {
    chrome?: { webview?: { postMessage: (message: HostMessage) => void; addEventListener: (type: string, listener: (event: MessageEvent) => void) => void; removeEventListener: (type: string, listener: (event: MessageEvent) => void) => void } };
    nookCanvas?: { editor: Editor; receive: (message: HostMessage) => Promise<boolean>; snapshot: () => TLEditorSnapshot; state: () => object; sent: HostMessage[] };
  }
}

const native = window.chrome?.webview;
const preview = !native;
const initialPreferences = (): TLUserPreferences => {
  try { const stored = getUserPreferences(); return { ...stored, locale: stored.locale || 'zh-cn' }; }
  catch { return { id: 'nook-local-user', locale: 'zh-cn', colorScheme: 'light', color: '#7c5cce' }; }
};
// Only local editing preferences use the isolated WebView profile's storage.
// Project documents remain exclusively under the native durable store.
const userPreferences = atom<TLUserPreferences>('nook-canvas-user', initialPreferences());
const user = createTLUser({ userPreferences, setUserPreferences: next => {
  userPreferences.set(next);
  try { setUserPreferences(next); } catch { /* Unavailable preference storage must not block editing. */ }
} });
const assetUrls = getAssetUrls({ baseUrl: new URL('./tl-assets/', document.baseURI).href });
// These are Editor constructor dependencies. Recreating either on a React
// status/name render recreates the editor and abandons the load handshake.
const EDITOR_OPTIONS = { maxShapesPerPage: 100000, maxPages: 40 };
const NO_VIDEO_MIME_TYPES: string[] = [];
// Chromium suspends animation frames in background/minimized windows. Do not
// strand the native load/flush handshake merely because the editor is hidden.
const afterPaint = () => new Promise<void>(resolve => {
  let frame = 0, finished = false;
  const finish = () => { if (finished) return; finished = true; clearTimeout(timeout); cancelAnimationFrame(frame); resolve(); };
  const timeout = setTimeout(finish, 100);
  frame = requestAnimationFrame(() => { frame = requestAnimationFrame(finish); });
});
let hostBridge: CanvasBridge | null = null;

function NookEditSubmenu() {
  const editor = useEditor();
  const selectActive = useValue('select active', () => editor.getCurrentToolId() === 'select', [editor]);
  return <TldrawUiMenuSubmenu id="edit" label="menu.edit" disabled={!selectActive}>
    <UndoRedoGroup /><ClipboardMenuGroup /><CopyAsMenuGroup /><MiscMenuGroup />
    <TldrawUiMenuGroup id="lock"><ToggleLockMenuItem /><UnlockAllMenuItem /></TldrawUiMenuGroup>
    <TldrawUiMenuGroup id="select-all"><SelectAllMenuItem /></TldrawUiMenuGroup>
  </TldrawUiMenuSubmenu>;
}

function NookContextMenu() {
  const editor = useEditor();
  const selectActive = useValue('select active', () => editor.getCurrentToolId() === 'select', [editor]);
  return <DefaultContextMenu>{selectActive && <>
    <TldrawUiMenuGroup id="modify">
      <TldrawUiMenuSubmenu id="edit" label="context-menu.edit" size="small"><MiscMenuGroup /><ToggleLockMenuItem /></TldrawUiMenuSubmenu>
      <ArrangeMenuSubmenu /><ReorderMenuSubmenu /><MoveToPageMenu />
    </TldrawUiMenuGroup>
    <ClipboardMenuGroup /><CopyAsMenuGroup />
    <TldrawUiMenuGroup id="select-all"><SelectAllMenuItem /></TldrawUiMenuGroup>
  </>}</DefaultContextMenu>;
}

function NookMainMenu() {
  return <DefaultMainMenu>
    <TldrawUiMenuGroup id="nook-project">
      <TldrawUiMenuItem id="nook-add-images" label="nook.add-images" icon="tool-media" onSelect={() => { hostBridge?.post('pickImages'); }} />
      <TldrawUiMenuItem id="nook-new-project" label="nook.new-project" icon="plus" onSelect={() => { hostBridge?.post('newProject'); }} />
    </TldrawUiMenuGroup>
    <NookEditSubmenu />
    <ViewSubmenu />
    <PreferencesGroup />
    <TldrawUiMenuGroup id="nook-shortcuts">
      <KeyboardShortcutsMenuItem />
    </TldrawUiMenuGroup>
  </DefaultMainMenu>;
}

const components: TLComponents = {
  MainMenu: NookMainMenu, ContextMenu: NookContextMenu, StylePanel: NookStylePanel,
  SharePanel: null, DebugPanel: null, DebugMenu: null, CursorChatBubble: null,
};
const overrides: TLUiOverrides = {
  translations: { 'zh-cn': { 'nook.add-images': '添加图片', 'nook.new-project': '新建画布', 'spline-style.cubic': '平滑曲线', 'style-panel.dash': '线型' } },
  actions: (editor, actions) => {
    const result = { ...availableActions(actions), 'insert-media': { ...actions['insert-media'], onSelect: () => { hostBridge?.post('pickImages'); } } };
    const editableIds = () => editor.getSelectedShapes().filter(shape => !editor.isShapeOrAncestorLocked(shape)).map(shape => shape.id);
    for (const [id, operation] of [
      ['align-left', 'left'], ['align-center-horizontal', 'center-horizontal'], ['align-right', 'right'],
      ['align-top', 'top'], ['align-center-vertical', 'center-vertical'], ['align-bottom', 'bottom'],
    ] as const) result[id] = { ...actions[id], onSelect: () => {
      const ids = editableIds();
      if (!editor.isIn('select') || editor.getInstanceState().isReadonly || ids.length < 2) return;
      editor.mark(`nook ${id}`); editor.alignShapes(ids, operation);
    } };
    for (const operation of ['horizontal', 'vertical'] as const) {
      const id = `distribute-${operation}`;
      result[id] = { ...actions[id], onSelect: () => {
        const ids = editableIds();
        if (!editor.isIn('select') || editor.getInstanceState().isReadonly || ids.length < 3) return;
        editor.mark(`nook ${id}`); editor.distributeShapes(ids, operation);
      } };
    }
    return result;
  },
};

function isSafeImageSource(src: unknown): src is string {
  return safeSource(src, document.baseURI);
}

export function NookCanvas() {
  const bridgeRef = useRef<CanvasBridge | null>(null);
  const editorRef = useRef<Editor | null>(null);
  const [saveState, setSaveState] = useState(preview ? 'preview' : 'loading');
  const [message, setMessage] = useState<CanvasMessage | null>(null);
  const [empty, setEmpty] = useState(true);
  const sent = useRef<HostMessage[]>([]);
  const [name, setName] = useState('画布');
  const snapshot = () => editorRef.current!.getSnapshot();

  const onMount = useCallback((editor: Editor) => {
    editorRef.current = editor;
    applyCanvasPreferences(editor, undefined, false);
    const disposeWheel = installCanvasWheel(editor);
    let reportedToolsVisible: boolean | null = null;
    const reportToolVisibility = () => {
      const visible = !editor.getInstanceState().isFocusMode;
      if (visible === reportedToolsVisible) return;
      reportedToolsVisible = visible;
      bridge.post('toolVisibilityChanged', { visible });
    };
    const thumbnails = new CanvasThumbnailQueue({
      capture: () => captureCanvasThumbnail(editor),
      isCurrent: (token: { revision: number; generation: number; change: number }) =>
        bridge.loaded && !bridge.loading && bridge.generation === token.generation && bridge.revision === token.revision && bridge.change === token.change && bridge.savedChange === token.change,
      publish: (payload: { revision: number; dataUrl: string }) => bridge.post('thumbnail', payload),
    });
    // Assets are staged as inline data once. The host replaces them with durable local URLs.
    editor.store.props.assets.upload = async (_asset, file) => new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = () => reject(new Error('图片读取失败，请重试。'));
      reader.readAsDataURL(file);
    });
    editor.store.props.assets.resolve = async asset => {
      if (asset.type !== 'image' || !isSafeImageSource(asset.props.src)) return null;
      return asset.props.src;
    };
    editor.registerExternalContentHandler('url', () => setMessage({ text: '请先将网页图片收藏到素材库，再拖入画布。', source: 'notice' }));
    editor.registerExternalContentHandler('embed', () => setMessage({ text: '本地画布不加载外部网页。', source: 'notice' }));
    editor.registerExternalAssetHandler('url', null);
    editor.registerExternalContentHandler('svg-text', () => setMessage({ text: '请使用“添加图片”导入 SVG，软件会转换为可保存的预览图片。', source: 'notice' }));

    const bridge = new CanvasBridge({
      send: (hostMessage: HostMessage) => {
        if (native) native.postMessage(hostMessage);
        else {
          sent.current.push(hostMessage);
          if (hostMessage.type === 'save') queueMicrotask(() => {
            void bridge.receive({ ...hostMessage, type: 'saved', payload: { revision: Number(hostMessage.payload?.revision) + 1, assets: [] } });
          });
        }
      },
      capture: snapshot,
      apply: async (payload: HostPayload) => {
        if (payload.scene) {
          const previous = editor.getSnapshot();
          try { editor.loadSnapshot(payload.scene); }
          catch (error) { editor.loadSnapshot(previous); throw error; }
        }
        applyCanvasPreferences(editor, payload.canvasPreferences, payload.isNewProject === true);
        if (payload.theme === 'light' || payload.theme === 'dark') editor.user.updateUserPreferences({ colorScheme: payload.theme });
        setName(payload.name || '画布');
        setEmpty(editor.getCurrentPageShapes().length === 0);
        await afterPaint();
      },
      commit: async () => {
        // blur submits the native text editor, then complete commits in-progress tools.
        const active = document.activeElement;
        if (active instanceof HTMLTextAreaElement || active instanceof HTMLInputElement) active.blur();
        editor.complete();
        editor.setEditingShape(null);
        await afterPaint();
      },
      normalize: (payload: { assets?: { id: string; src: string }[] }, capturedScene: Scene) => {
        const replacements = assetSourceUpdates(payload, capturedScene, (id: string) => editor.getAsset(id as TLAssetId), document.baseURI) as TLAssetPartial[];
        // Remote source prevents the delayed store listener from treating ACK
        // normalization as a new user edit. It leaves native undo intact.
        if (replacements.length) editor.store.mergeRemoteChanges(() => withWritableEditor(editor, () => editor.run(() => editor.updateAssets(replacements), { history: 'ignore' })));
      },
      setTheme: (theme: string) => { if (theme === 'light' || theme === 'dark') editor.user.updateUserPreferences({ colorScheme: theme }); },
      setToolVisibility: (visible: boolean) => editor.updateInstanceState({ isFocusMode: !visible }),
      onLoaded: () => {
        reportedToolsVisible = null; reportToolVisibility();
        // Existing projects need a preview even when the user only opens
        // them. No edit or extra save is required to regenerate derived data.
        thumbnails.schedule({ revision: bridge.revision, generation: bridge.generation, change: bridge.savedChange });
      },
      onSaved: (token: { revision: number; generation: number; change: number }) => thumbnails.schedule(token),
      setInteraction: (enabled: boolean) => {
        if (!enabled) {
          const active = document.activeElement;
          if (active instanceof HTMLTextAreaElement || active instanceof HTMLInputElement) active.blur();
          editor.complete(); editor.setEditingShape(null);
        }
        editor.updateInstanceState({ isReadonly: !enabled });
      },
      insert: async (payload: ImportPayload) => {
        const items = payload.images || payload.files || payload.items || [];
        if (!items.length) throw new Error('没有可导入的图片。');
        const assets: TLImageAsset[] = items.map(item => {
          const props = item.props;
          const src = props?.src || item.src || item.assetUrl || item.dataURL;
          if (!isSafeImageSource(src)) throw new Error('图片地址不是当前画布的本地资源。');
          const id = (item.id?.startsWith('asset:') ? item.id : `asset:${item.fileId || item.id || crypto.randomUUID()}`) as TLImageAsset['id'];
          const w = Number(props?.w || item.width || item.w), h = Number(props?.h || item.height || item.h);
          if (!Number.isFinite(w) || !Number.isFinite(h) || w <= 0 || h <= 0) throw new Error('图片尺寸无效。');
          return { id, typeName: 'asset', type: 'image', meta: {}, props: { src, w, h, name: props?.name || item.name || '图片', mimeType: props?.mimeType || item.mimeType || 'image/png', isAnimated: props?.isAnimated || item.mimeType === 'image/gif' } };
        });
        const layout = planImageBatch(
          assets.map(asset => ({ w: asset.props.w, h: asset.props.h })),
          editor.getViewportPageBounds(),
          editor.getCurrentPageShapes().map(shape => editor.getShapePageBounds(shape)),
          payload.position,
        );
        const shapes = assets.map((asset, index) => {
          const { x, y, w, h } = layout.boxes[index];
          return { id: createShapeId(), type: 'image' as const, x, y, props: { assetId: asset.id, w, h } };
        });
        withWritableEditor(editor, () => {
          editor.mark('nook-insert-images');
          editor.run(() => {
            editor.createAssets(assets.filter(asset => !editor.getAsset(asset.id)));
            editor.createShapes(shapes).select(...shapes.map(shape => shape.id));
            const bounds = editor.getSelectionPageBounds();
            if (bounds) editor.zoomToBounds(bounds, { inset: 96, targetZoom: 1, immediate: true });
          });
        });
        return { count: shapes.length };
      },
      status: (state: string, text = '', source = 'notice') => {
        setSaveState(preview && state === 'saved' ? 'preview' : state);
        if (text) setMessage({ text, source: state === 'error' && source === 'save' ? 'save-error' : 'notice' });
        else if (state === 'saved') setMessage(current => current?.source === 'save-error' ? null : current);
      },
    });
    bridgeRef.current = bridge;
    hostBridge = bridge;
    const dispose = editor.store.listen(entry => {
      setEmpty(editor.getCurrentPageShapes().length === 0);
      const records = [
        ...Object.values(entry.changes.added),
        ...Object.values(entry.changes.removed),
        ...Object.values(entry.changes.updated).map(pair => pair[1]),
      ];
      const instanceChanged = Object.values(entry.changes.updated).some(([before, after]) => {
        if (before.typeName !== 'instance' || after.typeName !== 'instance') return false;
        return ['currentPageId', 'isFocusMode', 'exportBackground', 'isGridMode', 'isToolLocked'].some(key => (before as unknown as Record<string, unknown>)[key] !== (after as unknown as Record<string, unknown>)[key]);
      });
      if (bridge.loaded && !bridge.loading) reportToolVisibility();
      if (instanceChanged || records.some(record => ['shape', 'asset', 'binding', 'page', 'document', 'camera'].includes(record.typeName))) bridge.changed();
    }, { scope: 'all', source: 'user' });
    const listener = (event: MessageEvent) => { void bridge.receive(event.data); };
    native?.addEventListener('message', listener);
    bridge.ready();
    if (preview) {
      void bridge.receive({ protocol: PROTOCOL, type: 'load', session: crypto.randomUUID(), payload: { name: '画布预览', revision: 0, isNewProject: true } });
    }
    if (preview || new URLSearchParams(location.search).get('test') === '1') {
      window.nookCanvas = { editor, receive: value => bridge.receive(value), snapshot, state: () => ({ loaded: bridge.loaded, change: bridge.change, savedChange: bridge.savedChange, revision: bridge.revision, session: bridge.session }), sent: sent.current };
    }
    return () => {
      dispose(); disposeWheel(); thumbnails.dispose(); native?.removeEventListener('message', listener); bridge.dispose();
      bridgeRef.current = null; editorRef.current = null; hostBridge = null; delete window.nookCanvas;
    };
  }, []);

  useEffect(() => {
    const beforeUnload = (event: BeforeUnloadEvent) => {
      const bridge = bridgeRef.current;
      if (bridge && bridge.change !== bridge.savedChange) { event.preventDefault(); event.returnValue = ''; }
    };
    window.addEventListener('beforeunload', beforeUnload);
    return () => window.removeEventListener('beforeunload', beforeUnload);
  }, []);

  const labels: Record<string, string> = { loading: '正在加载', dirty: '待保存', saving: '正在保存', saved: '已保存', error: '保存失败', preview: '预览 · 不保存到图库' };
  return <main className="nook-canvas" aria-label={name}>
    <Tldraw onMount={onMount} user={user} assetUrls={assetUrls} components={components} overrides={overrides} autoFocus inferDarkMode={false} options={EDITOR_OPTIONS} acceptedImageMimeTypes={SUPPORTED_IMAGE_MIME_TYPES} acceptedVideoMimeTypes={NO_VIDEO_MIME_TYPES} />
    {empty && <div className="nook-empty" aria-hidden="true"><span>拖入图片，开始整理想法</span><small>中键 / 空格拖动画布 · Alt＋滚轮缩放 · 双击写字</small></div>}
    <div className={`nook-save-state nook-save-state--${saveState}`} role="status">{labels[saveState] || saveState}{saveState === 'error' && <button onClick={() => void bridgeRef.current?.save(undefined, true)}>重试</button>}</div>
    {message && <div className="nook-message" role="alert"><span>{message.text}</span><button aria-label="关闭提示" onClick={() => setMessage(null)}>×</button></div>}
  </main>;
}

class CanvasErrorBoundary extends React.Component<React.PropsWithChildren, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  componentDidCatch(error: Error) { hostBridge?.post('error', { message: error.message }); }
  render() { return this.state.failed ? <div className="nook-fatal">画布未能加载。原项目不会被覆盖，请关闭此窗口后重试。</div> : this.props.children; }
}
const root = document.getElementById('root');
if (root) createRoot(root).render(<CanvasErrorBoundary><NookCanvas /></CanvasErrorBoundary>);
