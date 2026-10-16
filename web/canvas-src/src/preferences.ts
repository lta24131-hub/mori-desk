import { DefaultDashStyle, DefaultFontStyle, DefaultSizeStyle, type Editor } from 'tldraw';

export type CanvasPreferences = {
  gridDefault?: boolean;
  toolsVisibleDefault?: boolean;
  snapMode?: boolean;
  zoomSpeed?: number;
  defaultDash?: 'solid' | 'draw' | 'dashed' | 'dotted';
  defaultSize?: 's' | 'm' | 'l' | 'xl';
  saveDelay?: number;
};

// Project grid/focus choices belong to the snapshot, not the global settings.
// Only the host can say that this is a newly created project.
export function applyCanvasPreferences(editor: Editor, value: CanvasPreferences | undefined, isNewProject: boolean) {
  const preferences = value && typeof value === 'object' ? value : {};
  if (isNewProject) {
    editor.updateInstanceState({
      isGridMode: preferences.gridDefault === true,
      isFocusMode: preferences.toolsVisibleDefault === false,
    });
  }
  editor.user.updateUserPreferences({ isSnapMode: preferences.snapMode === true });
  editor.setCameraOptions({
    wheelBehavior: 'pan',
    zoomSpeed: Number.isFinite(preferences.zoomSpeed) ? Math.min(2, Math.max(0.5, preferences.zoomSpeed!)) : 1,
  });
  const dash = ['solid', 'draw', 'dashed', 'dotted'].includes(preferences.defaultDash!) ? preferences.defaultDash! : 'solid';
  const size = ['s', 'm', 'l', 'xl'].includes(preferences.defaultSize!) ? preferences.defaultSize! : 'm';
  editor.setStyleForNextShapes(DefaultDashStyle, dash, { history: 'ignore' });
  editor.setStyleForNextShapes(DefaultSizeStyle, size, { history: 'ignore' });
  editor.setStyleForNextShapes(DefaultFontStyle, 'sans', { history: 'ignore' });
}
