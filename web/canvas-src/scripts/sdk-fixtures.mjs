import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  createTLStore, defaultShapeUtils, getSnapshot, loadSnapshot,
  GroupShapeUtil, GeoShapeGeoStyle, ArrowShapeArrowheadStartStyle,
  DefaultColorStyle, ArrowBindingUtil, getIndexAbove,
} from 'tldraw';

export function createFullSdkFixture() {
  const store = createTLStore({ shapeUtils: defaultShapeUtils });
  const utils = new Map([...defaultShapeUtils, GroupShapeUtil].map(Util => [Util.type, new Util(null)]));
  const mainPage = 'page:fixture-main', secondPage = 'page:fixture-second';
  store.put([
    store.schema.types.document.create({ id: 'document:document', name: '实际 SDK 全功能交叉测试' }),
    store.schema.types.page.create({ id: mainPage, name: '全部工具', index: 'a1' }),
    store.schema.types.page.create({ id: secondPage, name: '第二页', index: 'a2' }),
  ]);
  let count = 0;
  let index = 'a1';
  const shapes = [];
  function shape(type, props = {}, other = {}) {
    count++;
    index = getIndexAbove(index);
    const record = store.schema.types.shape.create({
      id: `shape:fixture-${count}`, type, parentId: mainPage, index,
      x: ((count - 1) % 8) * 180, y: Math.floor((count - 1) / 8) * 155,
      props: { ...utils.get(type).getDefaultProps(), ...props }, ...other,
    });
    shapes.push(record); return record;
  }
  for (const geo of GeoShapeGeoStyle.values) shape('geo', { geo, w: 145, h: 110, text: geo, fill: 'none', dash: 'solid', font: 'sans' });
  for (const color of DefaultColorStyle.values) shape('geo', { color, labelColor: color, fill: 'solid', dash: 'dotted', text: color, w: 145, h: 100, scale: .8 });
  const points = [{ x: 0, y: 0, z: .5 }, { x: 20, y: 45, z: .8 }, { x: 90, y: 25, z: .3 }];
  shape('draw', { segments: [{ type: 'free', points }], isComplete: true, color: 'violet', fill: 'none', dash: 'draw', scale: 1.25 });
  shape('draw', { segments: [{ type: 'straight', points }], isComplete: true, isClosed: true, color: 'blue', fill: 'pattern', dash: 'dashed' });
  shape('highlight', { segments: [{ type: 'free', points }], isComplete: true, color: 'yellow', scale: .8 });
  for (const spline of ['line', 'cubic']) shape('line', { spline, dash: 'solid', points: { a1: { id: 'a1', index: 'a1', x: 0, y: 0 }, a2: { id: 'a2', index: 'a2', x: 60, y: 60 }, a3: { id: 'a3', index: 'a3', x: 125, y: 10 } } });
  for (const head of ArrowShapeArrowheadStartStyle.values) shape('arrow', { arrowheadStart: head, arrowheadEnd: head, start: { x: 0, y: 0 }, end: { x: 145, y: 80 }, bend: head === 'none' ? 0 : 40, text: head, dash: 'solid', font: 'sans' });
  shape('note', { text: '便签\n中文与 English', font: 'sans', url: 'https://example.com/note', growY: 40, fontSizeAdjustment: 1, scale: 1.2 });
  shape('text', { text: '文本\n多行 English', font: 'serif', textAlign: 'middle', autoSize: false, w: 140, scale: 1.3 });
  const frame = shape('frame', { name: '分区', w: 310, h: 180 });
  shape('geo', { geo: 'ellipse', w: 120, h: 90 }, { parentId: frame.id, x: 25, y: 45 });
  const group = shape('group');
  shape('geo', { geo: 'rectangle', w: 100, h: 80 }, { parentId: group.id, x: 10, y: 10 });
  shape('text', { text: '分组文字', font: 'sans' }, { parentId: group.id, x: 25, y: 50 });
  const png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1kAAAAASUVORK5CYII=';
  store.put([store.schema.types.asset.create({ id: 'asset:fixture-png', type: 'image', props: { name: 'fixture.png', src: png, w: 1, h: 1, mimeType: 'image/png', isAnimated: false } })]);
  shape('image', { assetId: 'asset:fixture-png', w: 100, h: 100, crop: { topLeft: { x: .1, y: .1 }, bottomRight: { x: .9, y: .9 } }, flipX: true, flipY: false, url: 'https://example.com/image' });
  shape('geo', { geo: 'rectangle', w: 100, h: 50, text: '链接', url: 'https://example.com/geo' }, { parentId: secondPage, isLocked: true, opacity: .7, rotation: .2 });
  const bindArrow = shape('arrow', { start: { x: 0, y: 0 }, end: { x: 120, y: 50 } });
  const bindingProps = new ArrowBindingUtil(null).getDefaultProps();
  store.put([store.schema.types.binding.create({ id: 'binding:fixture-end', type: 'arrow', fromId: bindArrow.id, toId: shapes[0].id, props: { ...bindingProps, terminal: 'end', normalizedAnchor: { x: .5, y: .5 }, isPrecise: true } })]);
  store.put(shapes);
  store.ensureStoreIsUsable();
  const snapshot = getSnapshot(store);
  snapshot.session = { version: 0, currentPageId: mainPage, isFocusMode: false, exportBackground: true, isDebugMode: false, isToolLocked: false, isGridMode: true, pageStates: [mainPage, secondPage].map(pageId => ({ pageId, camera: { x: 20, y: 40, z: .75 }, selectedShapeIds: [], focusedGroupId: null })) };
  const roundtrip = createTLStore({ shapeUtils: defaultShapeUtils });
  loadSnapshot(roundtrip, snapshot);
  return snapshot;
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const snapshot = createFullSdkFixture();
  const output = process.argv[2] && resolve(process.argv[2]);
  if (output) { await mkdir(dirname(output), { recursive: true }); await writeFile(output, JSON.stringify(snapshot, null, 2) + '\n'); }
  const records = Object.values(snapshot.document.store);
  console.log(JSON.stringify({ output, shapes: records.filter(record => record.typeName === 'shape').length, geo: GeoShapeGeoStyle.values.length, arrowheads: ArrowShapeArrowheadStartStyle.values.length, colors: DefaultColorStyle.values.length, bindings: records.filter(record => record.typeName === 'binding').length }));
  process.exit(0); // The SDK opens a BroadcastChannel even in a headless process.
}
