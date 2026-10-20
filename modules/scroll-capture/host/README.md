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

Optional one-shot prewarming uses `--nook-warm <absolute warm.json>` with
`schemaVersion: 1`, a 32-hex `sessionId`, the same parent `outputDirectory`, and
a mandatory `parentProcessId`. It loads a single hidden 1 × 1 renderer, without
native screen initialization/enumeration, pixels, visible surfaces or global
Escape. Both document load and the adapter's final script handshake must finish
before `warm.ready.json` is published. A same-directory `request.json` may arrive
while it loads; activation requires matching session, directory, parent and
`deferredStart: true`. Only then are native monitor validation and Escape enabled;
the existing `prepared.json` / `start.request` handoff still gates capture.

An unused warm process expires after 120 seconds from host startup or exits
silently on cancellation/parent death (no `result.json`). An accepted request
gets a fresh 18-second startup deadline; an already-published valid request wins
the expiry race. Warm processes are never reused across capture sessions.
`component.json` advertises `capturePrewarm: true`. Optional
`startup-timing.json` contains only the session identity and elapsed phase values,
never source paths, URLs or pixels. Atomic temporary filenames are
`.<filename>.<helper PID>.tmp` and are removed in the writer's `finally` block.

Launch with `--nook-request <absolute request.json>`. The request must live in a
fresh per-session output directory. Coordinates are physical screen pixels;
the crop must be contained by the supplied single monitor. Monitor geometry
and DPI are rechecked before capture. `protocol.mjs` is the canonical schema
validator, and `protocol.d.mts` declares the C#/Node interchange fields.

Nook now requests `deferredStart: true` and verifies that the packaged
`component.json` advertises the same capability before launching. Keep the
original frozen screenshot and selection visible, with editing/output disabled
but cancellation available, during the helper's cold start. After monitor/DPI
validation, Escape registration and renderer loading, the hidden helper writes
`prepared.json` (`schemaVersion: 1`, current `sessionId`, `prepared: true`). It
does not dispatch `clip_init`, sample pixels or show capture surfaces yet.

The parent validates this message, asynchronously hides its current overlay,
allows one 40 ms compositor settle, then atomically writes `start.request`
(`schemaVersion: 1`, current `sessionId`, `start: true`). The helper consumes it
once through a startup-only 30 ms poll and then initializes capture. Both sides
reject mismatched/late/replayed messages, and cancellation/terminal state wins
over a start. The parent also rejects `ready` before start, including one that
arrives during the UI handoff. Prepared is not usable-ready and never extends
the startup watchdog. A failed startup restores the exact original crop, marks
and history; a cancelled one closes without reopening. This hides cold-loading
time behind the retained screenshot; it does not eliminate or benchmark native
first-frame latency. No permanently resident helper process is added.

Legacy callers omitting `deferredStart` still initialize immediately and must
hide their original overlay before launching. Pass editable marks separately
as `initialAnnotations`; do not burn them into
the desktop frame. They are restored over the first frame after stitching,
including the accumulated offset when scrolling upwards, and stay editable.
Mosaic PNGs may only be session-local `mark-N.png` files.

After initialization, Escape registration, the first rolling frame, and both
independent preview/toolbar surfaces loading, the helper atomically publishes `ready.json`.
Nook can request cancellation by
creating `cancel.request`; the optional `parentProcessId` also cancels an orphan
session. Escape is active only during this helper session, including while the
window passes mouse input through to the source application. During a native
Save dialog the global Escape hook is suspended so Escape cancels that dialog,
not the capture. In the optional editor Escape is local to the focused window.

Supported output actions are `copy`, `save`, `pin`, and `collect`; Nook performs
the chosen operation. With `transactionalOutputs: true`, the helper first
commits `export-N.png`, then `export-N.json`, and waits for Nook's matching
`export-N.reply.json`. Only an `ok` reply commits terminal `result.json` and
closes the session. A cancelled save dialog or failed output leaves the same
result (or open editable scene and undo history) available for retry. Save and
Finish/Copy never force the editor open; Edit explicitly opens it. Session IDs, export IDs, actions,
local paths, image limits, and reply schemas are validated on both sides;
terminal acknowledgement does not perform the output operation a second time.

If publishing an export manifest fails, only the exact newly-created PNG may
be rolled back, and only while the manifest is provably absent. Existing or
replaced files are never removed by this recovery. A verified committed pair
remains successful even if its final rename reports an error. If a capture view
fails after the parent has already written a matching successful reply, that
acknowledged output remains successful; missing or invalid replies do not.
Renderer error text is bounded to the host's message limits without dropping
the resume signal, so lengthy filesystem errors cannot strand retry controls.

Explicit cancellation while an export is pending hides all helper surfaces and
releases the global Escape hook immediately. Late load/update callbacks cannot
show them again. The export still waits for its matching destination reply:
success remains success, and failure/cancellation ends without reopening.
No timeout guesses whether a destination committed; if a live parent never
replies, the dismissed helper may continue waiting. Native Save-dialog Escape
is different: it cancels the dialog and retains the image for retry.

If the parent verified a full PNG but the helper or reply transport later fails,
the parent reloads that verified file into its existing recovery preview after
an unsuccessful output. Locally confirmed success only completes the session,
without repeating the output. Invalid files or failed presentation preserve the
original screenshot and marks; closed/replaced sessions cannot reopen.

Legacy nontransactional callers still receive `result.png` before terminal
`result.json`. Cancelled or failed sessions never promise an image. A single
terminal latch rejects late completions, stops native hooks/timers, restores
mouse input, and exits the helper. No user library is read or written by the
host.
Terminal cleanup arms its exit fallback before native calls and isolates each
cleanup step, so one throwing renderer/window cannot skip the remaining steps.

The accumulated image is limited to 64 million pixels and 32,768 pixels per
dimension, checked before resizing the accumulated canvas. At that limit the
valid partial image is retained with a visible warning; it is never silently
copied or saved as a complete capture. A subsequent explicit action can edit,
save or copy that retained result. A failed canvas-context allocation follows
the same partial-image warning path; if no first frame exists, it reports an
error rather than inventing an image. The full-monitor sampling
window is always click-through; it never switches hit testing on hover. A
separate compact, frameless native toolbar owns Edit/Save/Cancel/Finish. A second
frameless surface shows only the stitched image. Controls stay interactive
through every sample, so neither a quick first
click nor a delayed IPC reply can send a button click to the application below.
Input exclusion uses current OS window bounds, not stale renderer coordinates.
The controls open without taking keyboard focus from the source.

The preview is positioned beside the crop whenever space is available, with its
bottom aligned to the selection. A stable 212 × 48 DIP toolbar sits below and
right aligned to the crop, with an above/in-work-area fallback. The image yields
space to the controls for near-fullscreen crops. `panel-layout.mjs` owns the
pure desktop-DIP geometry; only the host translates monitor-relative crop DIP
to desktop coordinates. Windows content protection is enabled and checked on
the sampler/dimmer, preview and toolbar before loading their renderers; none
is hidden or made transparent during live sampling. The full-screen dimmer
stays steady, and sampling has no per-frame animation or paint timeout.
Exclusion by the actual capture API still requires desktop validation (Windows
10 2004 or later provides WDA_EXCLUDEFROMCAPTURE; older Windows may black out a
protected window instead). The protection flag alone does not prove pixel
exclusion on a particular capture driver. Explicit Edit removes protection
from the bounded result editor. Unchanged panel updates do not resize/re-show
native windows or reload the same preview image. Cancellation after an in-flight
native sample discards the late image before pixel conversion or matching.
Only initial setup and entering the editor use a bounded one-time paint settle.
Successful stitches send a whole-image PNG thumbnail capped at 480 × 2048 pixels.
It grows upward, then scales the whole image down to remain within the work
area; it never crops the start or tail. The original accumulated image remains
full resolution. Source scrolling schedules throttled frames plus a settled
trailing frame. Finishing freezes the result and hides the sampling overlay,
retaining the pure preview and toolbar. Explicit Edit opens a bounded movable,
resizable window with a horizontal tool strip and centered image. Native Save
temporarily hides the always-on-top controls to avoid covering the dialog;
cancellation/error restores them without showing the fullscreen sampling layer.

The controls accept only validated local status and PNG messages from the capture
renderer. Only the toolbar's own sender may request Edit/Save/Copy/Cancel; it
cannot send exports, read settings, or open remote URLs. Terminal cleanup
destroys all three windows, and stale messages cannot reopen controls.

## Verification

`../tests/protocol.test.mjs` uses only temporary fixture files.
`../tests/renderer-state.test.mjs` extracts the patched production functions
into a synthetic context; it does not launch Electron or access a display.
Runtime screenshot, mixed-DPI, input-routing, and visual checks still require
an explicitly authorized desktop test. Type checking/build success alone does
not establish that those interactive checks passed.

`../tests/host-lifecycle.test.mjs` runs the production host and its actual local
toolbar script with fake Electron boundaries. It covers the first button action
without hover, permanent native hit targets, current-window input exclusion,
trusted senders, duplicate finish, cancel, loading failures, Save-dialog Escape,
direct-output retry and cleanup.
`../tests/scroll-panel.test.mjs` runs the production renderer adapter and covers
bounded previews, source monitor layout, frame exclusion, cancellation and
retry feedback. These tests never create native windows or touch the clipboard.

`../tests/panel-layout.test.mjs` and `reference-flow-integration.test.mjs`
exercise the production geometry and complete renderer action chain, including
multiple monitor origins/scales, preview/toolbar non-overlap, initial editable
annotations, direct Copy/Save, retained results, and explicit Edit. Use
`tests/run-capture-regressions.ps1` from the repository root for the combined
helper/WPF background run. Its result explicitly keeps visual acceptance
pending; a passing background run is not permission to install or release.
