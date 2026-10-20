// Phase correlation is only a proposal: its downsampled global peak can select
// the wrong repeated row or direction when less than half a screen overlaps.
// Verify full-resolution content before the upstream composer mutates pixels.
let nookMatchUnresolved = false;
type NookMatchScore = { shift: number; error: number; evidence: number; rows: number; overlap: number };
type NookMatchSamples = { colors: Float32Array; texture: Uint8Array; columns: number; height: number };

function nookPixelDistance(a: Uint8ClampedArray, pa: number, b: Uint8ClampedArray, pb: number) {
    return (Math.abs(a[pa] - b[pb]) + Math.abs(a[pa + 1] - b[pb + 1]) + Math.abs(a[pa + 2] - b[pb + 2])) / 3;
}
function nookPixelTexture(data: Uint8ClampedArray, p: number, x: number, y: number, width: number, height: number) {
    return (x > 2 && nookPixelDistance(data, p, data, p - 12) > 14) ||
        (x + 3 < width && nookPixelDistance(data, p, data, p + 12) > 14) ||
        (y > 1 && nookPixelDistance(data, p, data, p - width * 8) > 14) ||
        (y + 2 < height && nookPixelDistance(data, p, data, p + width * 8) > 14);
}
function nookSampleFrame(frame: ImageData, grid: number): NookMatchSamples {
    const width = frame.width, height = frame.height, stepX = Math.max(1, Math.floor(width / grid));
    const columns = Math.ceil((width - 4) / stepX);
    const colors = new Float32Array(height * columns * 3), texture = new Uint8Array(height * columns);
    for (let y = 0; y < height; y++) for (let column = 0; column < columns; column++) {
        const x = 2 + column * stepX, p = (y * width + x) * 4, at = y * columns + column;
        texture[at] = nookPixelTexture(frame.data, p, x, y, width, height) ? 1 : 0;
        const before = (Math.max(0, y - 1) * width + x) * 4, after = (Math.min(height - 1, y + 1) * width + x) * 4;
        // A small vertical low-pass handles subpixel scroll/AA differences;
        // original pixel rows are never rescaled in the actual stitched image.
        for (let channel = 0; channel < 3; channel++)
            colors[at * 3 + channel] = (frame.data[before + channel] + 2 * frame.data[p + channel] + frame.data[after + channel]) / 4;
    }
    return { colors, texture, columns, height };
}
function nookScoreSamples(a: NookMatchSamples, b: NookMatchSamples, shift: number, grid = 48, top = 0, bottom = 0): NookMatchScore {
    const height = a.height, columns = a.columns;
    const start = Math.max(top, top - shift), end = Math.min(height - bottom, height - bottom - shift);
    const stepY = Math.max(1, Math.floor((end - start) / grid));
    let error = 0, evidence = 0, rows = 0;
    for (let y = start + 2; y < end - 2; y += stepY) {
        let rowEvidence = 0;
        for (let column = 0; column < columns; column++) {
            const pa = (y + shift) * columns + column, pb = y * columns + column;
            if (a.texture[pa] || b.texture[pb]) {
                for (let channel = 0; channel < 3; channel++) error += Math.abs(a.colors[pa * 3 + channel] - b.colors[pb * 3 + channel]) / 3;
                evidence++; rowEvidence++;
            }
        }
        if (rowEvidence > 1) rows++;
    }
    return { shift, error: evidence ? error / evidence : Infinity, evidence, rows, overlap: end - start };
}
function nookStaticBands(a: ImageData, b: ImageData) {
    const maximum = Math.floor(a.height / 4);
    function identicalRow(y: number) {
        for (let x = 0; x < a.width; x++) {
            const p = (y * a.width + x) * 4;
            if (nookPixelDistance(a.data, p, b.data, p) > 4 || a.data[p + 3] !== b.data[p + 3]) return false;
        }
        return true;
    }
    function texturedBand(start: number, end: number) {
        let rows = 0, count = 0;
        for (let y = start; y < end; y++) {
            let inRow = 0;
            for (let x = 2; x < a.width - 2; x += Math.max(1, Math.floor(a.width / 80)))
                if (nookPixelTexture(a.data, (y * a.width + x) * 4, x, y, a.width, a.height)) inRow++;
            if (inRow > 1) rows++;
            count += inRow;
        }
        return rows >= 3 && count >= 16;
    }
    let top = 0, bottom = 0;
    while (top < maximum && identicalRow(top)) top++;
    while (bottom < maximum && identicalRow(a.height - bottom - 1)) bottom++;
    if (top < 6 || top >= maximum || !texturedBand(0, top)) top = 0;
    if (bottom < 6 || bottom >= maximum || !texturedBand(a.height - bottom, a.height)) bottom = 0;
    return { top, bottom };
}
function nookVerticalMatches(a: ImageData, b: ImageData, top = 0, bottom = 0) {
    if (a.width !== b.width || a.height !== b.height || a.width < 16 || a.height < 48) return [];
    const contentHeight = a.height - top - bottom;
    const limit = contentHeight - Math.max(48, Math.ceil(contentHeight * .18));
    const scores: NookMatchScore[] = [];
    const previous = nookSampleFrame(a, 48), next = nookSampleFrame(b, 48);
    for (let shift = -limit; shift <= limit; shift++) {
        const score = nookScoreSamples(previous, next, shift, 48, top, bottom);
        if (score.evidence >= 32 && score.rows >= 6) scores.push(score);
    }
    return scores.sort((a, b) => a.error - b.error || b.overlap - a.overlap);
}

function nookFindVerticalOffset(a: ImageData, b: ImageData) {
    if (a.width !== b.width || a.height !== b.height || a.data.length !== a.width * a.height * 4 || b.data.length !== b.width * b.height * 4) return null;
    // Returning to the last accepted viewport is a valid recovery even when it
    // is blank or repetitive; no pixels need to be added in this case.
    if (a.width > 0 && a.height > 0 && a.data.every((value, index) => value === b.data[index]))
        return { dx: 0, dy: 0, confidence: 1 };
    const { top, bottom } = nookStaticBands(a, b);
    const scores = nookVerticalMatches(a, b, top, bottom), best = scores[0];
    // One-pixel neighbours may be AA/subpixel equivalents; a different repeated
    // row is not. Never guess between two equally plausible content alignments.
    const alternative = scores.find(score => Math.abs(score.shift - best?.shift) > 1);
    if (!best || best.error > 12 || (alternative && alternative.error - best.error < 4)) return null;
    const dense = nookScoreSamples(nookSampleFrame(a, 160), nookSampleFrame(b, 160), best.shift, 160, top, bottom);
    if (dense.evidence < 64 || dense.rows < 6 || dense.error > 12) return null;
    return { dx: 0, dy: -best.shift, confidence: 1 - dense.error / 255 };
}

function nookScoreXY(a: ImageData, b: ImageData, shiftX: number, shiftY: number, grid: number) {
    const width = a.width, height = a.height;
    const left = Math.max(2, 2 - shiftX), right = Math.min(width - 2, width - 2 - shiftX);
    const top = Math.max(2, 2 - shiftY), bottom = Math.min(height - 2, height - 2 - shiftY);
    if (right - left < Math.max(32, Math.ceil(width * .18)) || bottom - top < Math.max(48, Math.ceil(height * .18))) return null;
    const stepX = Math.max(1, Math.floor((right - left) / grid)), stepY = Math.max(1, Math.floor((bottom - top) / grid));
    let error = 0, evidence = 0, rows = 0;
    for (let y = top; y < bottom; y += stepY) {
        let rowEvidence = 0;
        for (let x = left; x < right; x += stepX) {
            const pa = ((y + shiftY) * width + x + shiftX) * 4, pb = (y * width + x) * 4;
            if (!nookPixelTexture(a.data, pa, x + shiftX, y + shiftY, width, height) && !nookPixelTexture(b.data, pb, x, y, width, height)) continue;
            for (let channel = 0; channel < 3; channel++) {
                const before = channel - width * 4, after = channel + width * 4;
                const ca = (a.data[pa + before] + 2 * a.data[pa + channel] + a.data[pa + after]) / 4;
                const cb = (b.data[pb + before] + 2 * b.data[pb + channel] + b.data[pb + after]) / 4;
                error += Math.abs(ca - cb) / 3;
            }
            evidence++; rowEvidence++;
        }
        if (rowEvidence > 1) rows++;
    }
    return { shiftX, shiftY, error: evidence ? error / evidence : Infinity, evidence, rows };
}

async function nookFindXYOffset(a: ImageData, b: ImageData) {
    if (a.width !== b.width || a.height !== b.height || a.data.length !== a.width * a.height * 4 || b.data.length !== b.width * b.height * 4) return null;
    const proposal = await findOffset(a, b);
    if (!Number.isFinite(proposal.dx) || !Number.isFinite(proposal.dy)) return null;
    // Preserve upstream's optional two-axis mode, but only use its phase peak
    // to choose a bounded neighbourhood. Recheck the actual overlapping pixels.
    const radius = Math.min(8, Math.max(3, Math.ceil(Math.max(a.width, a.height) / 512)));
    const x = Math.round(-proposal.dx), y = Math.round(-proposal.dy);
    const candidates: NonNullable<ReturnType<typeof nookScoreXY>>[] = [];
    for (let dy = y - radius; dy <= y + radius; dy++) for (let dx = x - radius; dx <= x + radius; dx++) {
        const score = nookScoreXY(a, b, dx, dy, 48);
        if (score && score.evidence >= 64 && score.rows >= 6) candidates.push(score);
    }
    candidates.sort((a, b) => a.error - b.error);
    const best = candidates[0];
    if (!best || best.error > 12) return null;
    const alternative = candidates.find(score => Math.abs(score.shiftX - best.shiftX) > 1 || Math.abs(score.shiftY - best.shiftY) > 1);
    if (alternative && alternative.error - best.error < 4) return null;
    const dense = nookScoreXY(a, b, best.shiftX, best.shiftY, 160);
    if (!dense || dense.evidence < 128 || dense.rows < 6 || dense.error > 12) return null;
    return { dx: -best.shiftX, dy: -best.shiftY, confidence: 1 - dense.error / 255 };
}

async function nookFindOffset(a: ImageData, b: ImageData) {
    // The Nook capture protocol is vertical. Keep the mature phase engine only
    // as a candidate for the upstream optional XY mode, never blindly accept it.
    let match = nookFindVerticalOffset(a, b);
    if (longFX === "xy" && !match) match = await nookFindXYOffset(a, b);
    const previouslyUnresolved = nookMatchUnresolved;
    nookMatchUnresolved = !match;
    if (!match) nookMatchProblem("未找到可靠重叠，请往回滚一点再慢慢继续");
    else if (previouslyUnresolved) nookRefreshPanel();
    return match;
}
