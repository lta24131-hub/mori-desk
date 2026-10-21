Nook scrolling-capture background regression checks
===================================================

Run from the Nook repository root:
  node --test modules/scroll-capture/tests/*.test.mjs

For the reviewed complete background capture suite against a fresh app build:
  .\tests\run-capture-regressions.ps1 -BuildDirectory <candidate> -NodePath <node.exe>
The wrapper runs the helper checks and the allowlisted WPF capture checks,
records duration and the candidate hash, and leaves real desktop acceptance
explicitly pending. It never uses build.ps1 -Test or -CaptureTests. The latter
are broad runners and must not be substituted for a background-only request.

Required for host/renderer checks: TypeScript and the clean pinned eSearch
checkout (15.6.0, commit 6f3f56241cccc8ada2c503df642566322dc67a3e).
The default checkout is outputs/esearch-15.6.0-source. To use another checkout,
set NOOK_ESEARCH_SOURCE to its absolute directory before running the tests.
The real picture_match 0.1.4 phase-aliasing comparison resolves first from
scroll-capture/node_modules/picture_match in a reviewed VM test package, then
from the upstream checkout's node_modules. The VM copy contains the unchanged
package manifest, license and dist files, including its actual WASM engine;
no phase matcher is mocked or skipped when that dependency is unavailable.
The production helper does not need this extra test copy: Vite already bundles
the engine into resources/app/out/renderer/assets/pic_match-*.js.
The protocol-only test does not require TypeScript or an upstream checkout:
  node --test modules/scroll-capture/tests/protocol.test.mjs

These tests never launch Electron or Nook, capture a display, install hooks,
operate a desktop, use the clipboard, upload data, or open the real library.
All file fixtures are synthetic data in fresh, uniquely named temporary
directories, removed after each test.

What is exercised
-----------------
- protocol.test.mjs: production request validation, session binding, local-only
  annotation paths, all seven initial annotation kinds, ready handshake,
  bounded PNG output and exact local result path, terminal/late completion.
  Export publication failures leave a retryable session, roll back only the
  exact unpublished PNG created by that attempt, and preserve published or
  replaced files. A committed manifest is not falsely reported as a failure.
- host-lifecycle.test.mjs: actual main.ts code transpiled into a VM. Only the
  Electron transport/window boundary, monitor metadata, timers, and parent
  liveness probe are substituted. Actual protocol writes are exercised.
  It checks one-Escape cancellation, trusted IPC, late completion, watchdogs,
  parent cancellation/death, passthrough cleanup, remote-request blocking,
  and Electron's first-assignment synchronous-reply semantics (including
  getMousePos coordinates, compatibility queries, and terminal null replies).
  A matching successful output reply survives a subsequent view failure;
  missing, rejected, malformed, and mismatched replies never imply success.
  Cleanup exceptions cannot skip the exit fallback or other native surfaces.
  Explicit pending-output cancellation hides every surface immediately and
  blocks late load callbacks, while awaiting an actual transaction reply.
  The no-reply case never fabricates cancellation or retries an output.
  The sampler must be capture-protected before renderer load; unavailable
  protection fails explicitly. Repeated same-state updates must not re-show
  or resize native surfaces or reload the same preview image. Save cancellation
  still restores the ready controls, and Edit removes sampling protection.
- renderer-state.test.mjs: actual adapter applied to the pinned upstream
  renderer. TypeScript AST extraction executes production functions against
  synthetic canvas/transport boundaries, not a separately written state model.
  It verifies retained upstream matching core, busy/disposed guards, negative
  monitor/DPI coordinate conversion, late-match cancellation, upward canvas
  offset handling, memory limits before allocation, wheel-state guards, and
  keeping rolling overlays stable while sampling. One-time startup paint
  settling is bounded even when animation callbacks stop, but later samples
  never wait for a paint. A native sample completed after cancellation is
  discarded before pixel conversion and the serial busy guard is released.
  It also exercises the real pointer-poll callback against a terminal null
  reply and prevents synchronous polling after renderer disposal.

- scroll-panel.test.mjs: the production renderer adapter executes against a
  synthetic DOM. Checks the whole-image preview bounds, source selection/DPI
  conversion, input filtering, steady capture-excluded sampling, output action dispatch,
  and cancellation. Actual native-window behavior is not inferred from it.
  Twenty consecutive samples record zero dimmer opacity writes and no paint
  waits; failures and late native completions cannot resurrect the controls.
- scroll-schedule.test.mjs: production scheduling with a synthetic clock checks
  throttled capture, a settled trailing frame, external busy work, no concurrent
  captures, and disposal/finish generation guards.
- panel-layout.test.mjs: production desktop-DIP geometry checks pure preview
  bottom anchoring, whole-image growth/scaling, stable four-button positions,
  negative monitor coordinates, fullscreen fallback and invalid input.
- reference-flow-integration.test.mjs: composed production finalization,
  annotation restoration, output and optional Edit flow. Direct Copy/Save do
  not open the editor. Cancelled/failed output can be retried; late encoding
  cannot revive a cancelled capture. A size cap retains the partial result and
  requires a new explicit output action, including when reached while finishing.
  Canvas-context allocation failure also retains and warns about an existing
  partial image without silently exporting it; first-frame failure has no result.
  A 1600-character output error crosses the actual reply reader, renderer,
  and host receiver without losing the resume signal or disabling retry.

- scroll-match.test.mjs: production matching uses both generated pixels and
  offline WPF-rendered text fixtures. It checks overlap verification, repeated
  content rejection, fixed headers/footers, subpixel scrolling, and seams.
- benchmark-match.mjs: optional offline matcher timings, not hotkey-to-overlay
  latency or a guarantee about an arbitrary live page.

2026-10-19 reference candidate background result
-----------------------------------------------
Candidate: outputs/nook19-reference3 (later used for interrupted desktop acceptance;
not installed or released, superseded by the anti-flicker candidate below).
Evidence: outputs/capture-regression-reference19-c1/result.json
Node helper: 142 tests passed, 0 failed, 0 skipped.
WPF capture: 16 allowlisted groups, 3992 checks passed.
Combined runner elapsed: 11.39 seconds (not a screenshot-startup measurement).
The evidence records both application and helper hashes. Earlier candidate
results do not establish acceptance of this reference redesign.
Generated main/clip graph: strict TypeScript check, 4 roots / 404 source files,
0 diagnostics. This is scoped to the shipped entrypoints, not all upstream apps.

The WPF suite now calls the actual CaptureWindow.ExportScrollingImage adapter
using synthetic pixels and fake output actions (no native window/clipboard).
Save cancellation/failure retains the same full snapshot for a subsequent Copy;
a closed source refuses queued output. An isolated mutation changing cancelled
Save into success fails the new relevant assertion; baseline and restored
output-lifecycle group both pass 186 checks. Evidence is under
outputs/scroll-adapter-proof19-a1/{baseline-narrow,mutant-narrow,restored-narrow}.

Full-image recovery now has an additional source-confirmed no-coverage test set:
the actual reply-file write fails after a cancelled/failed Save, then the parent
presents the verified full PNG through an inert presenter seam. Confirmed
Copy/Save/Collect never repeats or opens a retry preview after broken transport.
Damaged/unverified files or failed presentation restore original pixels, crop
and marks. Closed/replaced/quitting sessions refuse late recovery. These cases
use unshown WPF objects, fake output destinations and unique temporary files;
they are not a real-screen recovery demonstration or mutation-survival proof.

Limits
------
These are background contract/state checks, not desktop visual acceptance.
They do not prove native global-key routing on every PC, real browser scroll
timing, pixel-perfect overlap matching on every page, display-driver capture,
or the appearance/editability of every migrated Fabric annotation. Those
require a separate controlled desktop session, with the user's permission.

2026-10-19 anti-flicker candidate background result
--------------------------------------------------
Candidate: outputs/nook19-reference4 (not installed or released; a later
isolated fixture attempt did not establish controlled visual acceptance).
Helper build: outputs/scroll-helper-reference19-d1.
Evidence: outputs/capture-regression-reference19-d1/result.json.
Node helper: 146 tests passed, 0 failed, 0 skipped.
WPF capture: 16 allowlisted groups, 3992 checks passed.
Combined runner elapsed: 10.12 seconds (not live-capture latency).
Generated main/clip graph strict TypeScript: 4 roots / 404 source files,
0 diagnostics. No desktop windows, clipboard or real library were used.
New regression checks expose the former per-frame opacity/paint waits and
redundant resize/show/image reload calls. Native compositor exclusion and
actual perceived smoothness remain unverified; do not install this candidate
solely because the background suite passed. The earlier real Save-cancel
button-state observation also still needs an actual retry-click check.

2026-10-19 deferred-start handoff background result
-------------------------------------------------
Candidate: outputs/nook19-reference6 (not launched, installed or released).
Helper build: outputs/scroll-helper-reference19-e1.
Evidence: outputs/capture-regression-reference19-e2/result.json.
Node helper: 159 tests passed, 0 failed, 0 skipped.
WPF capture: 16 allowlisted groups, 4098 checks passed.
Combined runner elapsed: 11.47 seconds (not screenshot-startup latency).
Generated main/clip graph strict TypeScript: 4 roots / 404 source files,
0 diagnostics. All 510 shipped helper files match the fresh helper build.

New checks cover prepared/start sequencing, no native initialization or helper
surface display before start, old-component capability rejection, late/duplicate
messages, file replacement/reparse/size boundaries, cancellation and startup
deadlines. WPF tests use the actual atomic start-publication method with unique
synthetic directories and unshown capture objects. Pending and late UI callbacks
cannot publish start; a verified cancelled terminal is recognized before child
teardown, so one Esc at the deadline does not intentionally reopen the old crop.
Preparing input tests use real command routes and raised button events, assert
unchanged pixels/crop/marks/history/defaults, and exercise failure recovery both
before and after handoff. Failed ordinary outputs also restore history and size
button availability. No clipboard, product window or real library was used.

The latest user said they had not manually tested and would do so later.
Desktop tests remain closed; perceived transition smoothness, first-frame pixel
exclusion and actual native cancel/output behavior are still pending. This
background pass is not installation or release approval.
