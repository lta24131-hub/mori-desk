Nook scrolling-capture background regression checks
===================================================

Run from the Nook repository root:
  node --test modules/scroll-capture/tests/*.test.mjs

Required for host/renderer checks: TypeScript and the clean pinned eSearch
checkout (15.6.0, commit 6f3f56241cccc8ada2c503df642566322dc67a3e).
The default checkout is outputs/esearch-15.6.0-source. To use another checkout,
set NOOK_ESEARCH_SOURCE to its absolute directory before running the tests.
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
- host-lifecycle.test.mjs: actual main.ts code transpiled into a VM. Only the
  Electron transport/window boundary, monitor metadata, timers, and parent
  liveness probe are substituted. Actual protocol writes are exercised.
  It checks one-Escape cancellation, trusted IPC, late completion, watchdogs,
  parent cancellation/death, passthrough cleanup, remote-request blocking,
  and Electron's first-assignment synchronous-reply semantics (including
  getMousePos coordinates, compatibility queries, and terminal null replies).
- renderer-state.test.mjs: actual adapter applied to the pinned upstream
  renderer. TypeScript AST extraction executes production functions against
  synthetic canvas/transport boundaries, not a separately written state model.
  It verifies retained upstream matching core, busy/disposed guards, negative
  monitor/DPI coordinate conversion, late-match cancellation, upward canvas
  offset handling, memory limits before allocation, wheel-state guards, and
  hiding rolling overlays before sampling.
  It also exercises the real pointer-poll callback against a terminal null
  reply and prevents synchronous polling after renderer disposal.

- scroll-panel.test.mjs: the complete production panel is executed against a
  synthetic DOM. Checks persistent controls, width-fit preview, tail following,
  left/right/fullscreen/DPI placement, drag clamps, native input-island routing,
  selection filtering, transparent sampling without removing hit targets, and
  idempotent cancellation. No real browser/window is launched.
- scroll-schedule.test.mjs: production scheduling with a synthetic clock checks
  throttled capture, a settled trailing frame, external busy work, no concurrent
  captures, and disposal/finish generation guards.

Latest result: 2026-10-16 — 63 tests passed, 0 failed, 0 skipped.

Limits
------
These are background contract/state checks, not desktop visual acceptance.
They do not prove native global-key routing on every PC, real browser scroll
timing, pixel-perfect overlap matching on every page, display-driver capture,
or the appearance/editability of every migrated Fabric annotation. Those
require a separate controlled desktop session, with the user's permission.
