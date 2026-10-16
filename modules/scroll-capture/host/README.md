# Nook single-session scrolling capture host

This optional helper reuses eSearch 15.6.0's capture editor and image-matching
algorithm at commit `6f3f56241cccc8ada2c503df642566322dc67a3e`. The generated
derivative retains its GPL-3.0 license. It does not provide eSearch's tray,
updater, OCR, translation, or network features.

## Build boundary

Run `prepare.mjs --upstream <fixed checkout> --stage <new empty directory>`.
Preparation verifies the exact upstream commit and copies sources to a separate
staging directory; it never modifies that checkout. Install or link the pinned
upstream dependencies in the stage, then build its generated package. The
repository's build/package scripts are responsible for shipping Electron,
native dependencies, the generated `out` tree, assets, and required `lib` files.

The renderer is trusted local application code. Navigation, new windows,
permissions, and network requests are blocked by the minimal Electron main.
Only the capture renderer can send completion commands. Keep this helper
separate from untrusted web content.

## One capture session

Launch with `--nook-request <absolute request.json>`. The request must live in a
fresh per-session output directory. Coordinates are physical screen pixels;
the crop must be contained by the supplied single monitor. Monitor geometry
and DPI are rechecked before capture. `protocol.mjs` is the canonical schema
validator, and `protocol.d.mts` declares the C#/Node interchange fields.

Hide the original Nook screenshot overlay **before** launching this helper.
Pass editable marks separately as `initialAnnotations`; do not burn them into
the desktop frame. They are restored over the first frame after stitching,
including the accumulated offset when scrolling upwards, and stay editable.
Mosaic PNGs may only be session-local `mark-N.png` files.

After initialization, Escape registration, and the first rolling frame, the
helper atomically publishes `ready.json`. Nook can request cancellation by
creating `cancel.request`; the optional `parentProcessId` also cancels an orphan
session. Escape is active only during this helper session, including while the
window passes mouse input through to the source application.

Completion commits `result.png` before terminal `result.json`. Supported actions
are `copy`, `save`, `pin`, and `collect`; Nook performs the chosen operation.
Cancelled or failed sessions never promise an image. A single terminal latch
rejects late completions, stops native hooks/timers, restores mouse input, and
exits the helper. No user library is read or written by the host.

The accumulated image is limited to 64 million pixels and 32,768 pixels per
dimension, checked before resizing the accumulated canvas. At that limit the
valid partial image is retained for editing/saving. A persistent movable panel
contains explicit Finish/Cancel actions and a width-fit, scrollable preview.
Native input passes through everywhere outside the panel; scrolling the source
selection schedules throttled frames plus a settled trailing frame. Capture
temporarily makes overlapping controls (including shadows) transparent without
removing their DOM or native hit targets, keeping them out of captured pixels.

## Verification

`../tests/protocol.test.mjs` uses only temporary fixture files.
`../tests/renderer-state.test.mjs` extracts the patched production functions
into a synthetic context; it does not launch Electron or access a display.
Runtime screenshot, mixed-DPI, input-routing, and visual checks still require
an explicitly authorized desktop test. Type checking/build success alone does
not establish that those interactive checks passed.
