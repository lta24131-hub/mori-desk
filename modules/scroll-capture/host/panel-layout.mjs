// All rectangles are desktop DIP. Image dimensions only supply an aspect ratio.
// The preview is the complete image, bottom anchored to the capture selection;
// it shrinks as one image once the available height is exhausted.
export function layoutCaptureControls({ selection, workArea, imageWidth = selection?.width, imageHeight = selection?.height }) {
    for (const rect of [selection, workArea]) {
        if (!rect || ![rect.x, rect.y, rect.width, rect.height].every(Number.isFinite) || rect.width <= 0 || rect.height <= 0)
            throw new RangeError("Capture controls require finite, positive rectangles");
    }
    if (![imageWidth, imageHeight].every(value => Number.isFinite(value) && value > 0))
        throw new RangeError("Preview image dimensions must be positive");
    const clamp = (value, min, max) => Math.max(min, Math.min(value, max));
    const margin = Math.min(8, Math.floor(Math.min(workArea.width, workArea.height) / 4));
    const left = workArea.x + margin, right = workArea.x + workArea.width - margin;
    const top = workArea.y + margin, bottom = workArea.y + workArea.height - margin;
    const gap = 12;
    const desiredWidth = Math.min(420, selection.width * 0.4, right - left);
    const rightSpace = Math.max(0, right - (selection.x + selection.width + gap));
    const leftSpace = Math.max(0, selection.x - gap - left);
    let previewSide = rightSpace >= desiredWidth ? "right" : leftSpace >= desiredWidth ? "left" : rightSpace >= leftSpace ? "right" : "left";
    let availableWidth = previewSide === "right" ? rightSpace : leftSpace;
    // A near-fullscreen selection has no outside space. Keep the whole preview
    // on the source monitor, rather than leaking into a different DPI monitor.
    if (availableWidth < 24) { previewSide = "inside"; availableWidth = right - left; }
    const previewBottom = clamp(selection.y + selection.height, top + 1, bottom);
    const maxHeight = Math.max(1, previewBottom - top);
    let scale = Math.min(desiredWidth / imageWidth, availableWidth / imageWidth, maxHeight / imageHeight);
    const width = Math.max(1, Math.floor(imageWidth * scale));
    const height = Math.max(1, Math.floor(imageHeight * scale));
    const previewX = previewSide === "right" ? selection.x + selection.width + gap : previewSide === "left" ? selection.x - gap - width : right - width;
    const preview = { x: Math.round(clamp(previewX, left, right - width)), y: Math.round(previewBottom - height), width, height };
    const toolbarWidth = Math.min(212, Math.floor(right - left));
    const toolbarHeight = Math.min(48, Math.floor(bottom - top));
    const below = selection.y + selection.height + 10;
    const toolbarY = below + toolbarHeight <= bottom ? below : selection.y - 10 - toolbarHeight;
    const toolbar = {
        x: Math.round(clamp(selection.x + selection.width - toolbarWidth, left, right - toolbarWidth)),
        y: Math.round(clamp(toolbarY, top, bottom - toolbarHeight)), width: toolbarWidth, height: toolbarHeight,
    };
    const overlaps = preview.x < toolbar.x + toolbar.width && preview.x + preview.width > toolbar.x &&
        preview.y < toolbar.y + toolbar.height && preview.y + preview.height > toolbar.y;
    if (overlaps) {
        // Fullscreen selections leave neither outer side free. Preserve the
        // stable toolbar and bottom anchor; the image yields horizontal space
        // first, then vertical space on an unusually narrow display.
        const besideToolbar = toolbar.x - gap - left;
        if (besideToolbar >= 24) {
            scale = Math.min(scale, besideToolbar / imageWidth);
            preview.width = Math.max(1, Math.floor(imageWidth * scale));
            preview.height = Math.max(1, Math.floor(imageHeight * scale));
            preview.x = Math.round(toolbar.x - gap - preview.width);
        } else {
            scale = Math.min(scale, Math.max(1, previewBottom - toolbar.y - toolbar.height - 10) / imageHeight);
            preview.width = Math.max(1, Math.floor(imageWidth * scale));
            preview.height = Math.max(1, Math.floor(imageHeight * scale));
            preview.x = Math.round(right - preview.width);
        }
        preview.y = Math.round(previewBottom - preview.height);
    }
    return { toolbar, preview, previewSide, scale };
}
