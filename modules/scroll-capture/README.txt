Nook scrolling capture — eSearch adaptation
===========================================

This component is a Windows x64, single-session helper derived from eSearch
15.6.0, commit 6f3f56241cccc8ada2c503df642566322dc67a3e. It is not a second
resident screenshot application. Nook remains responsible for the shortcut,
initial region selection, library import, clipboard, saving and persistent pins.

Build (PowerShell, Node 24 and pnpm 10.17.0 required)
------------------------------------------------
  .\modules\scroll-capture\build.ps1 -OutputDirectory C:\build\nook-capture
  .\build.ps1 -OutputDirectory C:\build\nook -ScrollCaptureDirectory C:\build\nook-capture

The output directories must be new. NodePath and PnpmPath can be supplied when
they are not on PATH. PnpmPath accepts either pnpm.cmd or pnpm.mjs. The source
restore is pinned and dependencies use the original frozen pnpm-lock.yaml.
Lifecycle scripts are disabled. Only a checksum-verified Electron runtime is
downloaded separately. Upstream electron-builder/beforePack is never run.
The build does not launch an editor, take screenshots, or install input hooks.

Packaging copies only the capture renderer plus Electron, node-screenshots,
the Windows native binding, uiohook-napi and node-gyp-build. The build-time
checkout may contain other upstream dependencies; they are not shipped as
runtime OCR, recording, translation or segmentation packages.

Local session contract
----------------------
  Nook.ScrollCapture.exe --nook-request C:\...\<session-id>\request.json

The request uses schemaVersion 1 and a fresh 32-hex sessionId. Region and monitor
rectangles are physical desktop pixels, including negative origins. The monitor
also carries scaleFactor. Seven initial annotation types are passed in image
coordinates and imported as editable objects after stitching, clipped to the
first frame. Mosaic PNG sidecars belong to the same session directory.

ready.json confirms initialization. result.png is written before result.json;
the result is atomically committed and echoes the sessionId. Terminal states
are ok, cancelled and error. Actions are copy, save, pin and collect. The helper
never opens the personal library and never uses clipboard polling to signal
completion. Parent cancellation and parent-process loss end only this helper.
Nook rejects stale sessions, unexpected paths, invalid images and oversized
output; a helper failure restores the original screenshot and annotations.

Licensing and rebuilding the included source
--------------------------------------------
See NOTICES.md and LICENSE-eSearch.txt in the runtime. The complete adapted
source is included under corresponding-source/, with the original lockfile,
upstream-package.json and nook-adaptation/ scripts. To reproduce the runtime,
run nook-adaptation/build.ps1 from a Nook source checkout (or pass explicit new
UpstreamDirectory and OutputDirectory paths). The adapter checks the upstream
commit and each patch location; it fails closed on source drift.

The eSearch-derived helper is GPL-3.0. Process separation is an architecture
choice, not a claim to waive license obligations. Preserve corresponding source
and notices when redistributing. Electron and native dependency notices remain
in the payload, including libuiohook's LGPL notices and source/build material.

Acceptance status
-----------------
Compilation, protocol/state tests and dependency loading can be checked without
desktop access. They do not establish real scrolling accuracy, mixed-DPI monitor
behavior, overlay exclusion or responsiveness. These require a separate approved
desktop acceptance run before replacing the installed application.
