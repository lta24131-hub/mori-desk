import {
  DefaultStylePanel, DefaultStylePanelContent, TldrawUiButton,
  TldrawUiMenuContextProvider, TldrawUiMenuItem, useActions, useEditor,
  useRelevantStyles, useValue, type TLUiStylePanelProps,
} from 'tldraw';

const OPACITY_PRESETS = [0.1, 0.5, 1] as const;
const ALIGNMENT_ACTIONS = ['align-left', 'align-center-horizontal', 'align-right', 'align-top', 'align-center-vertical', 'align-bottom'] as const;
const DISTRIBUTION_ACTIONS = ['distribute-horizontal', 'distribute-vertical'] as const;

function OpacityPresets() {
  const editor = useEditor();
  const opacity = useValue('nook opacity presets', () => editor.getSharedOpacity(), [editor]);
  const readonly = useValue('nook opacity readonly', () => editor.getInstanceState().isReadonly, [editor]);
  if (!opacity || readonly) return null;
  return <div className="nook-opacity-presets" role="group" aria-label="透明度快捷选项">
    {OPACITY_PRESETS.map(value => <TldrawUiButton
      key={value} type="icon" title={`透明度 ${Math.round(value * 100)}%`}
      aria-label={`透明度 ${Math.round(value * 100)}%`}
      aria-pressed={opacity.type !== 'mixed' && Math.abs(opacity.value - value) < 0.001}
      data-testid={`nook.opacity.${value}`}
      onClick={() => {
        editor.mark('nook opacity preset');
        editor.run(() => {
          if (editor.isIn('select')) editor.setOpacityForSelectedShapes(value);
          editor.setOpacityForNextShapes(value);
          editor.updateInstanceState({ isChangingStyle: true });
        });
      }}>
      <span className="nook-opacity-icon" aria-hidden="true"><span style={{ opacity: value }} /></span>
    </TldrawUiButton>)}
  </div>;
}

function AlignmentPresets() {
  const editor = useEditor();
  const actions = useActions();
  const count = useValue('nook alignable selection', () => {
    if (!editor.isIn('select') || editor.getInstanceState().isReadonly) return 0;
    return editor.getSelectedShapes().filter(shape => !editor.isShapeOrAncestorLocked(shape)).length;
  }, [editor]);
  if (count < 2) return null;
  return <div className="nook-alignment-presets" aria-label="对齐和等距排列">
    <TldrawUiMenuContextProvider type="icons" sourceId="style-panel">
      <div className="nook-alignment-row">{ALIGNMENT_ACTIONS.map(id => <TldrawUiMenuItem key={id} {...actions[id]} />)}</div>
      {count >= 3 && <div className="nook-distribution-row">{DISTRIBUTION_ACTIONS.map(id => <TldrawUiMenuItem key={id} {...actions[id]} />)}</div>}
    </TldrawUiMenuContextProvider>
  </div>;
}

// Preserve every native fill/dash/font/geometry/arrow/curve control. These are
// quick entry points, not a reduced replacement for the mature style panel.
export function NookStylePanel(props: TLUiStylePanelProps) {
  const styles = useRelevantStyles();
  return <DefaultStylePanel {...props}>
    {styles && <OpacityPresets />}
    <DefaultStylePanelContent styles={styles} />
    <AlignmentPresets />
  </DefaultStylePanel>;
}
