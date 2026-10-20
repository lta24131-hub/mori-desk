import assert from "node:assert/strict";
import test from "node:test";
import { layoutCaptureControls } from "../host/panel-layout.mjs";

const workArea = { x: 0, y: 0, width: 1920, height: 1200 };
const selection = { x: 122, y: 131, width: 1036, height: 933 };
test("reference layout: pure image sits 12 DIP beside selection, toolbar below its right edge", () => {
    const layout = layoutCaptureControls({ selection, workArea, imageWidth: 1036, imageHeight: 933 });
    assert.deepEqual(layout.preview, { x: 1170, y: 691, width: 414, height: 373 });
    assert.deepEqual(layout.toolbar, { x: 946, y: 1074, width: 212, height: 48 });
    assert.equal(layout.previewSide, "right");
});
test("whole image grows upward then scales down without changing toolbar or bottom anchor", () => {
    const initial = layoutCaptureControls({ selection, workArea, imageWidth: 1036, imageHeight: 933 });
    const longer = layoutCaptureControls({ selection, workArea, imageWidth: 1036, imageHeight: 2000 });
    const veryLong = layoutCaptureControls({ selection, workArea, imageWidth: 1036, imageHeight: 18000 });
    assert.equal(longer.preview.width, initial.preview.width);
    assert.ok(longer.preview.y < initial.preview.y);
    assert.ok(veryLong.preview.width < longer.preview.width);
    for (const layout of [initial, longer, veryLong]) {
        assert.deepEqual(layout.toolbar, initial.toolbar);
        assert.equal(layout.preview.y + layout.preview.height, 1064);
        assert.ok(layout.preview.y >= 8);
    }
    assert.ok(Math.abs(veryLong.preview.width / veryLong.preview.height - 1036 / 18000) < 0.002);
});
test("left-side fallback and above-selection toolbar remain inside negative-coordinate source monitor", () => {
    const area = { x: -1920, y: -1080, width: 1920, height: 1040 };
    const crop = { x: -1000, y: -900, width: 950, height: 850 };
    const layout = layoutCaptureControls({ selection: crop, workArea: area, imageWidth: 1900, imageHeight: 4000 });
    assert.equal(layout.previewSide, "left");
    assert.equal(layout.preview.x + layout.preview.width, crop.x - 12);
    assert.ok(layout.toolbar.y + layout.toolbar.height <= crop.y - 10);
    for (const rect of [layout.preview, layout.toolbar]) {
        assert.ok(rect.x >= area.x && rect.y >= area.y);
        assert.ok(rect.x + rect.width <= area.x + area.width && rect.y + rect.height <= area.y + area.height);
    }
});
test("full-monitor crop retains a whole-image preview on the same monitor", () => {
    const layout = layoutCaptureControls({ selection: workArea, workArea, imageWidth: 1920, imageHeight: 20000 });
    assert.equal(layout.previewSide, "inside");
    assert.ok(layout.preview.width <= 420 && layout.preview.height <= 1184);
    assert.ok(layout.preview.x + layout.preview.width <= 1912);
    assert.ok(layout.preview.x + layout.preview.width <= layout.toolbar.x - 12 || layout.preview.y >= layout.toolbar.y + layout.toolbar.height + 10,
        "the whole-image fallback never obscures its controls");
});
test("malformed dimensions never reach native window bounds", () => {
    for (const imageHeight of [NaN, Infinity, 0, -1])
        assert.throws(() => layoutCaptureControls({ selection, workArea, imageWidth: 100, imageHeight }), RangeError);
    assert.throws(() => layoutCaptureControls({ selection: { ...selection, x: NaN }, workArea }), RangeError);
});
