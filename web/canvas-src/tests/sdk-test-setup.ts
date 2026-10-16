import { vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// JSDOM supplies the DOM, not browser layout/fonts/canvas. Only those browser
// primitives are replaced; the actual React component, SDK Editor, store,
// selection, history, camera and bridge execute unchanged.
Object.defineProperty(window, 'matchMedia', { value: vi.fn(query => ({ matches: false, media: query, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent() { return false; } })) });
class TestResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', TestResizeObserver);
class TestFontFace {
  status = 'loaded';
  loaded = Promise.resolve(this);
  load() { return this.loaded; }
}
vi.stubGlobal('FontFace', TestFontFace);
Object.defineProperty(document, 'fonts', { value: { ready: Promise.resolve(), add() {}, delete() {}, check() { return true; }, load() { return Promise.resolve([]); }, addEventListener() {}, removeEventListener() {} } });
vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function () {
  const width = this.classList.contains('tl-container') || this.classList.contains('tl-canvas') ? 1100 : 40;
  const height = this.classList.contains('tl-container') || this.classList.contains('tl-canvas') ? 760 : 40;
  return { x: 0, y: 0, left: 0, top: 0, right: width, bottom: height, width, height, toJSON() { return this; } };
});
vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(() => ({
  measureText(text: string) { return { width: text.length * 8, fontBoundingBoxAscent: 12, fontBoundingBoxDescent: 3, actualBoundingBoxAscent: 12, actualBoundingBoxDescent: 3 }; },
  resetTransform() {}, scale() {}, save() {}, restore() {}, beginPath() {}, fill() {}, stroke() {}, fillRect() {}, clearRect() {}, drawImage() {},
  createPattern() { return null; }, getImageData() { return { data: new Uint8ClampedArray(4) }; },
}) as never);
vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockImplementation(() => 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1kAAAAASUVORK5CYII=');
vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation(callback => callback(new Blob(['fixture'], { type: 'image/png' })));
Object.defineProperty(HTMLImageElement.prototype, 'decode', { value() { return Promise.resolve(); } });
vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => setTimeout(() => callback(performance.now()), 0));
vi.stubGlobal('cancelAnimationFrame', (id: ReturnType<typeof setTimeout>) => clearTimeout(id));
vi.stubGlobal('fetch', vi.fn(async (input: string | URL | Request) => {
  const address = String(input);
  const translation = address.match(/\/translations\/([a-z-]+)\.json/);
  if (translation) return new Response(readFileSync(resolve(process.cwd(), 'node_modules/@tldraw/assets/translations', `${translation[1]}.json`), 'utf8'), { status: 200 });
  return new Response('<svg xmlns="http://www.w3.org/2000/svg"></svg>', { status: 200 });
}));
Object.defineProperty(URL, 'createObjectURL', { value: () => 'blob:test-resource', configurable: true });
Object.defineProperty(URL, 'revokeObjectURL', { value: () => {}, configurable: true });
Object.defineProperty(HTMLElement.prototype, 'setPointerCapture', { value() {} });
Object.defineProperty(HTMLElement.prototype, 'releasePointerCapture', { value() {} });
Object.defineProperty(HTMLElement.prototype, 'hasPointerCapture', { value() { return false; } });
window.scrollTo = () => {};
