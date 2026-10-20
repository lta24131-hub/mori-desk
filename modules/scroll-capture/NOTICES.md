# Nook scrolling-capture helper — third-party notices

This separately packaged helper adapts the eSearch screenshot editor and reuses
its long-screenshot matching, stitching, preview, annotation, and editing code.
The eSearch-derived helper is licensed under the GNU General Public License,
version 3. This notice does not replace the individual license texts.

## eSearch provenance and corresponding source

- Project: https://github.com/xushengfeng/eSearch
- Author: xushengfeng / xsf and upstream contributors
- Version: `15.6.0`
- Fixed source commit: `6f3f56241cccc8ada2c503df642566322dc67a3e`
- Upstream license: `GPL-3.0`; the original full `LICENSE` is retained in the
  corresponding-source tree and copied with the packaged helper.

Nook adaptations include the isolated Electron host and local file protocol,
selected-region handoff, initial-annotation migration, cancellation/cleanup,
capture-size limits, a narrowed editor interface, and local export actions.
These are changes to upstream, not an unmodified official eSearch distribution.
Nook is not affiliated with or endorsed by the upstream authors.

The packaged helper's `corresponding-source/` directory contains the complete
modified eSearch source stage (excluding generated output and dependencies),
the exact dependency lockfile, and `nook-adaptation/` with the host, adaptation
scripts, build instructions, and tests needed to reproduce the helper. The
upstream source URL alone is not the corresponding-source delivery. Retain
these files, the license texts, and original copyright notices when sharing
the helper.

## Dependencies used by the capture editor

The exact installed versions below were checked against the pinned source's
installed dependency metadata. Code bundled into renderer JavaScript still
requires its notices even when it is not an external runtime package.

| Component | Version | License / notice |
| --- | --- | --- |
| `@erase2d/fabric` | 1.2.1 | MIT; omitted npm license text supplied in `licenses/erase2d-fabric-LICENSE.txt` |
| `bmp-js` | 0.1.0 | MIT |
| `chroma-js` | 3.1.1 | BSD-3-Clause AND Apache-2.0; includes ColorBrewer notices |
| `dkh-ui` | 0.14.2 | Apache-2.0 |
| `edge_det` | 0.1.3 | Apache-2.0 |
| `fabric` | 7.4.0 | MIT |
| `hotkeys-js` | 3.13.7 | MIT |
| `picture_match` | 0.1.4 | Apache-2.0, verified from its shipped `LICENSE` (no package license field) |
| `xtranslator` | 1.6.0 | Apache-2.0; used by upstream translation infrastructure, not an online-translation feature here |
| `node-screenshots` | 0.2.8 | Apache-2.0, including the selected Windows native package |
| `uiohook-napi` | 1.5.4 | MIT wrapper; bundled `libuiohook` is LGPL-3.0 and retains its own copyright/source notices |
| `node-gyp-build` | 4.8.1 | MIT; locked runtime native-loader dependency of `uiohook-napi` |
| Fluent UI System Icons, Checkmark 24 Regular | Retrieved 2026-10-19 | MIT, Copyright (c) 2020 Microsoft Corporation; original geometry retained, rendered as an alpha mask |

The Checkmark asset comes from https://github.com/microsoft/fluentui-system-icons.
Its exact source URL and MIT license are in `licenses/MicrosoftFluentSystemIcons/`
and in the corresponding source's `nook-adaptation/host/assets/fluent/`.

Individual JavaScript license copies are in the packaged helper's `licenses/`
directory. Native package licenses and source/build material are also retained
under `resources/app/node_modules/`. In particular,
`uiohook-napi/libuiohook/COPYING.LESSER.md` and `COPYING.md` must not be discarded
or described merely as an MIT dependency.

Electron and its bundled Chromium/Node.js components retain Electron's root
`LICENSE` and `LICENSES.chromium.html` notices. The helper does not download OCR
models, translation models, FFmpeg, or segmentation models, and must not invoke
the full upstream packaging download hook.

The helper blocks HTTP/HTTPS network requests and exports only to its current
local session directory. It does not upload screenshots or access the user's
Nook material library directly; the parent application applies the selected
local action after validating the returned session and image.
