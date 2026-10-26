File fallback icons for Nook

Real Windows thumbnails remain the primary presentation. These icons are shown only when a thumbnail is unavailable.
The application brand remains app.ico (an unchanged copy of assets/nook.ico).
Every advertised preview extension has an uppercase-named ICO. Each file contains 16, 24, 32, 48, 64, 128 and 256 px PNG-backed 32-bit RGBA entries.
16 px uses a family glyph and color bar instead of unreadable text. 24 px and above show the exact extension; MARKDOWN uses two lines.
Rebuild: powershell -NoProfile -File tools\Build-FileIcons.ps1
Editable SVG designs are in svg\. The generator is authoritative for size-specific raster artwork.
Artwork is original to this project, distributed under the repository license. No external fonts or third-party icons are bundled. Segoe UI is used from Windows during generation.
