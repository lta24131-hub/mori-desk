import { getSvgAsImage, type Editor } from 'tldraw';

// Use the SDK renderer rather than a screenshot of the current camera. Every
// shape on the page is represented, including work outside the visible area.
export async function captureCanvasThumbnail(editor: Editor): Promise<string> {
  const ids = [...editor.getCurrentPageShapeIds()];
  if (!ids.length) return '';
  const result = await editor.getSvgString(ids, { background: true, padding: 24, scale: 1 });
  if (!result || !Number.isFinite(result.width) || !Number.isFinite(result.height) || result.width <= 0 || result.height <= 0) throw new Error('画布缩略图无效。');
  const fit = Math.min(1, 480 / result.width, 300 / result.height);
  const blob = await getSvgAsImage(editor, result.svg, {
    type: 'png', quality: 1, scale: 1,
    width: Math.max(1, Math.floor(result.width * fit)),
    height: Math.max(1, Math.floor(result.height * fit)),
  });
  if (!blob || blob.size > 2 * 1024 * 1024) throw new Error('画布缩略图过大。');
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(new Error('画布缩略图读取失败。'));
    reader.readAsDataURL(blob);
  });
}
