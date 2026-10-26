# Windows file previews

Nook's native Windows shell wrapper and document icons are built from the source
in `modules/shell-preview`. The Nook application icon is unchanged.

SVG thumbnails use the unmodified **resvg 0.49.0** official Windows x64 binary:
https://github.com/linebender/resvg/releases/tag/v0.49.0

resvg is copyright its contributors, dual-licensed under the MIT and Apache 2.0
licenses. The original license texts accompany this component under `licenses/`.
Pinned source and executable provenance are recorded in `upstream.json`.
Transitive dependency notices for the exact upstream Cargo.lock are included
under `licenses/dependencies/`; `licenses/dependency-notices.json` records each pinned
crate and notice hash. This conservative bundle includes optional/platform
dependencies as well, rather than claiming they all execute on Windows.
Source, dependency inventory and acknowledgements:
https://github.com/linebender/resvg/tree/v0.49.0

The wrapper rejects SVG external resources and active/embedded content. Rendering
runs in a limited worker; unsupported or failed previews fall back to file icons.
Existing third-party thumbnail handlers and Windows default choices are retained.
