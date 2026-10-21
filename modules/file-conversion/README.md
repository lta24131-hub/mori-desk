# Experimental file conversion worker

This is the experimental conversion component shipped with Nook 0.1.26 preview.
The release uses the c11 VM-tested host and matching worker; see
`../../docs/Nook0.1.26发布说明.txt` for the exact verification boundaries.
Do not describe every Filee format as validated. The supplier download/install/
update/uninstall flows and every engine/format are not fully accepted end to end.

## Scope

Reuse Filee v1.12.0 Core and Engines in a short-lived .NET 10 subprocess, independent
of Nook's .NET Framework process. The upstream Avalonia app, installer, global input
hooks, tray, file watchers and UI are never built or started.

All Filee Core/Engines converters are registered, with availability checks and the
upstream three-hop route planner. Built-ins include image/PDF/vector, text/OOXML,
spreadsheet, data, subtitles, email, fonts, CAD, 3D and ebooks. Only actually available
routes to writable registry formats are advertised; this is not a promise that every
known format or malformed file will convert. Capabilities do not fully decode inputs.

FFmpeg, Ghostscript, LibreOffice, Pandoc, calibre, rhwp and 7-Zip are optional.
The default core does not bundle these large engines. An explicit component-management
request installs reviewed, pinned packages in `%LOCALAPPDATA%\Nook\ConversionComponents`;
conversion and capability requests never download anything automatically. Previously
bundled `engines/` components remain supported. The worker never discovers system
programs or starts Filee's application. Missing components are returned as
`missingEngines` and actionable `missingComponentIds`, not presented as working targets. Magick external
delegates, filters and non-allowlisted coders remain disabled by an isolated policy.

Deliberately withheld pending an explicit output-bundle/options contract: directory
extraction, OBJ output and resource-bearing HTML/Markdown edges, video frame exports,
and LibreOffice's first-page-only drawing exports. If an engine unexpectedly creates
sidecars, the task fails before publication instead of silently dropping dependencies.

The upstream `Filee.Engines` project is still monolithic: non-enabled managed
libraries may exist in build output. This is not a fully trimmed package. A later
split can reduce package size after functional validation; do not enable trimming
without testing reflection/native dependencies.

## Build (no machine-wide installation)

Use an existing .NET 10 SDK, or an independently SHA512-verified official SDK ZIP
extracted into an ignored workspace directory. No installer is required.

```powershell
.\build.ps1 -DotnetPath 'C:\path\to\isolated-sdk\dotnet.exe'
```

To bundle reviewed external engines, add
`-BundledEngineDirectory 'C:\path\to\verified\engines'`. Only pinned component names
are accepted; `.downloads` is excluded. Every `.filee-component` marker must match
the pinned upstream SHA-256. `component.json` records engine package provenance and
each bundled file's relative path, size and SHA-256 for host/package verification.
The build never runs an engine or installs one machine-wide.

The build fetches only the exact source archive in `upstream.json`, verifies SHA256,
extracts a fresh copy, restores NuGet packages from nuget.org, and compiles only
Core + Engines + this worker. It does not run Filee's build scripts or solution.
Build outputs, SDKs, NuGet packages and upstream source stay outside tracked source.
`Patch-Upstream.ps1` applies a hash-guarded discovery adapter: explicit managed roots
also work for rhwp/7-Zip/JRE, and developer-checkout/other-app discovery is disabled.
The adapter is included with the component's notices for reproducibility.

Reviewed build inputs at the pinned commit: `Directory.Build.props`,
`Directory.Packages.props`, Core and Engines csproj. They have no custom MSBuild
tasks, Exec targets or imports. Standard SDK/NuGet restore/build is still code
execution; only use a trusted SDK and pinned packages.

## Protocol v1

Run one hidden process per task:

```text
Nook.FileConversion.exe --request C:\absolute\new-session\request.json
```

Request (UTF-8 JSON, at most 64 KiB):

```json
{
  "schemaVersion": 1,
  "id": "0123456789abcdef0123456789abcdef",
  "operation": "convert",
  "inputs": ["C:\\Pictures\\example.png"],
  "target": "jpg",
  "quality": 90,
  "outputDirectory": "C:\\absolute\\new-session\\output",
  "responsePath": "C:\\absolute\\new-session\\response.json"
}
```

`capabilities` accepts 1–100 inputs; omit `target` and `outputDirectory`.
`convert` accepts exactly one input. Nook owns batch sequencing and cancellation.
`quality` is optional, defaults to 90 and must be 1–100. It controls JPG/WebP
lossy encoding. Hide this control for PNG/BMP/TIFF/GIF/PDF; those targets have
different encoding semantics, and this protocol does not promise a quality slider.
All paths must be ordinary absolute local paths, not UNC/device paths, ADS, symlinks
or reparse directories. Maximum input size is currently 8 GiB. The response must
be a new file beside the request. The output directory must be nonexistent or empty.

Response is atomically committed to `responsePath` and is also the only stdout JSON:

```json
{
  "schemaVersion": 1,
  "id": "0123456789abcdef0123456789abcdef",
  "operation": "convert",
  "status": "ok",
  "targets": [{"id":"jpg","label":"JPG","extension":"jpg","warning":"..."}],
  "files": [{
    "source": "C:\\Pictures\\example.png",
    "format": "png",
    "targets": ["jpg","webp","bmp","tiff","gif","pdf"],
    "outputs": ["C:\\absolute\\new-session\\output\\converted-example.jpg"],
    "warnings": ["JPG 使用有损压缩，透明区域会填成白色。"]
  }],
  "outputs": ["C:\\absolute\\new-session\\output\\converted-example.jpg"],
  "missingEngines": [],
  "missingComponentIds": [],
  "components": [],
  "error": null
}
```

`targets` is the common target list, permitting already-matching inputs which the
host skips rather than re-encodes; at least one input must require conversion.
`files` has each input's own targets. `missingEngines` contains actionable missing
component descriptions. Registry primary extensions are returned, including compound
extensions such as `tar.gz` and `kepub.epub`. Error is
`{"code":"...","message":"..."}`. `status` is `ok`, `error` or `cancelled`.
Exit codes: 0 success, 1 runtime/conversion failure, 2 invalid request, 3 cancelled.
Invalid requests have stdout errors only if no response destination was validated.

### Optional component management

The same schema accepts `components-list`, `components-install`, `components-update`
and `components-uninstall`. Their `inputs` must be empty; install/update/uninstall
require a reviewed package `componentId`. The component list contains `id`, `name`,
`description`, `installedVersion`, `availableVersion`, `downloadBytes`,
`installedBytes`, `state`, `canUninstall`, `dependencies` and `message`.
States are `missing`, `installed`, `update-available`, `broken`, or `newer_version`.
The last state means a newer Nook approved the installed package; this worker neither
executes nor downgrades it. Update Nook first, or explicitly uninstall that package.
Only Nook-reviewed embedded version pins are used; update does not blindly run the
newest upstream release. Existing bundled copies cannot be removed by this manager.

New managed snapshots include an anchored `.nook-component-integrity.json` inventory
of key executable/runtime files (programs, DLLs, Python code, JARs and related native
code). Installation hashes these files once. Settings reads the bounded inventory
and file metadata only; it does not repeatedly read gigabytes of package contents.
Before providing managed paths for conversion, and before taking the same-version
"already installed" shortcut, the worker verifies the recorded key-file SHA256s.
Changed or missing key files are repairable by reinstalling the pinned package.
This is not a full installed-data fidelity check or a security boundary against
another process with the same user's write access to the active manifest/store.

Older snapshots with no integrity anchor keep the previous compatibility checks
and remain usable; they are explicitly marked `update-available` / pending
verification with a reinstall-and-verify action, even if the version is unchanged.
Explicit update reinstalls from the original approved archive and creates the new
inventory. Deleting the inventory from a **new** anchored snapshot is an error,
not a way to silently return to legacy mode. Newer-component downgrade protection
continues to apply before repair.

### Component download network boundary

All component requests require HTTPS on port 443, without URL credentials or
fragments. Initial URLs are app-embedded pins. Redirects may remain on their own
supplier or use these exact reviewed delivery hosts:

- GitHub Releases: `release-assets.githubusercontent.com`, `objects.githubusercontent.com`.
- conda-forge: `conda.anaconda.org`, `anaconda.org`, `binstar-cio-packages-prod.s3.amazonaws.com`.
- calibre: `download.calibre-ebook.com` (same-origin delivery).
- LibreOffice: `mirror.aarnet.edu.au` (official TDF-listed mirror, fixed direct URL).

The LibreOffice pin keeps the upstream version, SHA256 and expected byte count;
only the download endpoint changes, avoiding the TDF dispatcher's arbitrary global
mirror redirects. Read-only supplier HEAD checks on 2026-10-20 confirmed GitHub's
release-assets redirect, direct conda/calibre responses, and AARNet's LibreOffice
26.2.6 response (200, 373252096 bytes). These are endpoint checks, not complete
package-download/conversion acceptance tests. A supplier/CDN change needs a reviewed
Nook update; it is not silently approved from an arbitrary Location header.

Before each GET and before accepting a redirect, DNS must contain only public
addresses. Direct socket connections resolve/check again and connect to the
checked IP while TLS authenticates the original hostname, preventing a separate
unvalidated hostname lookup. The configured **system proxy is retained and trusted
by the user**, including a local proxy. Local DNS checks cannot constrain what that
proxy resolves or routes remotely; this is not absolute SSRF protection through a
user-configured proxy. Download size and archive SHA256 remain mandatory in both
cases. No authentication credentials are added by the component downloader.

The synthetic component-store suite covers downgrade/foreign redirects (zero
second transport requests), initial/redirect private DNS, mixed DNS answers,
accepted GitHub CDN delivery, same-length/same-timestamp EXE/DLL damage, missing
inventory, legacy compatibility and explicit repair. It must run inside the
dedicated VM; compilation alone does not establish that those cases passed.

Every request may specify an ordinary absolute local `componentRoot` for an isolated
test store; normal use omits it. Tests must supply a workspace-only root and must not
write the user's real component store. Install/update progress is atomically written
to the request session's fixed `progress.json`, containing `stage`, `componentId`,
`bytesDone`, `bytesTotal`, `fraction` and a short `message`. This is diagnostic progress;
only the final validated response confirms success.

Conversion/capability work holds a shared cross-process lease while using immutable
component snapshots. Install/update/uninstall use an exclusive lease and report busy
when components are in use. Uninstallation concerns only managed engine files, never
the source files or conversion results.

Only accept a matching id/schema/operation, exit code 0 and `status:ok`; validate
every output remains in the chosen output directory. Never import partial results.
The host must place the process in a kill-on-close Windows Job Object before allowing
it to run. Termination is the hard cancellation guarantee, including native decoders.
Creating `cancel.flag` beside the request requests cooperative cancellation (100 ms
poll); native decoding may finish its current call first.

Native processing uses the short `%TEMP%\Nook-ConversionNative\<request-id>` directory
because some native codecs still reject long Windows paths. It is created only when
absent, rejects reparse parents, records the exact owning session in `owner-session.txt`,
and is removed on normal exit. The host must also reclaim that exact derived directory
after forced termination, but only after verifying the owner-session marker. Final
publication still uses the caller's chosen output directory and never overwrites sources.

This first protocol uses indeterminate progress. There is no resident process or
per-frame event stream. A fresh session is required for every request, including
capabilities. The host should delete its own exact session directory after exit,
not source files. On cancellation or failure, uncommitted staging files may remain;
they must not be accepted as successful results. Final files are copied to a fresh
sibling transaction directory, validated (at most 1000 files / 16 GiB), and committed
with one directory rename. Cancellation after commit does not hide successful
outputs. The original source is held read-only through conversion and publication.

## Fidelity and user-facing warnings

- PSD/PSB: composite image, no layers or editable effects.
- Animation: animated targets preserve animation where supported. Static targets
  export every coalesced frame, and PDF/TIFF preserve all frames as pages. APNG in a
  `.png` file is recognized. Static exports do not preserve animation playback/timing.
- ICO/CUR/DDS: maximum-size variant only, with an explicit warning.
- PDF: rasterized at 150 DPI, normally one image per page; no editable text layer.
- JPG: lossy, transparency becomes white. GIF: up to 256 colours.
- Engine is Magick Q8: high-bit-depth/HDR fidelity is not promised.
- Video-to-video retains the first video and audio streams; additional audio tracks,
  subtitles and attachments are not retained, with a visible target warning.
- EML/MSG-to-ZIP extracts attachments only, not the email body, with a visible warning.
- Markdown, HTML, SVG and SVGZ must be self-contained: scripts, active content and
  external resource dependencies are rejected, not fetched or silently dropped.
  Safe HTML outputs receive a restrictive Content-Security-Policy. SVGZ expansion is
  capped at 32 MiB and XML DTD/entity resolution is disabled.
- LibreOffice uses per-task profiles with all macro runtimes, active OLE/DDE content
  and automatic link/field/chart updates disabled. Modern Office/ODF packages with
  external resource relationships are rejected before opening the conversion engine.
  This is defense in depth, not a complete document-engine sandbox; legacy binary
  formats cannot receive the same ZIP/XML resource preflight.
- Never overwrite/delete/move source files. Results are new files with collision-safe
  names. Retain originals even after a successful conversion.

## Licensing and provenance

Filee own code is MIT: see `LICENSE-Filee.txt` and `upstream.json`. Third-party
dependencies have separate terms. Build copies upstream third-party notices,
available NuGet license/notice files and package metadata into the output. This is
not a substitute for a release license review, especially ImageMagick native codecs
and any MPL/LGPL transitive packages. Optional executable engines have their own
GPL/AGPL/MPL/other obligations; process isolation does not waive redistribution
terms. The release owner must include appropriate notices and corresponding source
or source offers where required.

Official source: https://github.com/KnifeLemon/Filee/tree/49911afab6eef81f144543710f1aa8021c617ff1

Official notices: https://github.com/KnifeLemon/Filee/blob/49911afab6eef81f144543710f1aa8021c617ff1/THIRD-PARTY-NOTICES.md

## Background regression tests

```powershell
.\test.ps1 -WorkerPath 'C:\path\to\worker\Nook.FileConversion.exe'
```

Tests create generated fixtures in a fresh ignored workspace folder. They never
touch the desktop, installed Nook, real library or Filee configuration. Coverage:
all seven output types, JPG quality changes and white transparency background,
two-page PDF page count/order/colour, multi-page TIFF preservation, a PSD composite
and flattening warning, GIF→WebP→GIF frame preservation, GIF→PNG/PDF all-frame
preservation, mixed-format target handling, unsupported/corrupt/coder-spoofed
files, source hash preservation, existing-output refusal, invalid requests, early
and in-conversion cancellation, missing PDFium dependency and process exit.
Also covers Markdown→HTML, TXT→DOCX, JSON→YAML, SRT→VTT, SVG→PDF, STL→GLB,
PDF→PPTX semantics and missing FFmpeg reporting. External engine availability is
not accepted as proof of real conversion; separate fixture tests are required.

The generated test corpus is deliberately small and deterministic. It does not
prove fidelity for all real PSD variants, malformed files, HDR or unusual codecs.
Broader sample-file tests and host Job Object cancellation are required before
releasing the feature to users.

`test-engines.ps1 -WorkerPath ...` additionally performs real, generated-fixture
conversions using the bundled engines when present: MP4→WebM→audio, MP3→FLAC,
RST→DOCX, TXT→PDF, EPS→PDF and ZIP↔7z, verifying stream types/duration, text,
archive bytes, original hashes and native-workspace reclamation. It also exercises
unsafe/self-contained markup handling. Absent engines are reported as skipped;
availability or extension counts are not a substitute for tested conversions.
When LibreOffice is bundled, the suite additionally checks ODT→DOC→PDF→text,
CSV→XLS→XLSX→CSV, a document-load macro fixture that must not execute, and external
ODF/OOXML-template resources that must fail before any HTTP request. With calibre,
TXT→EPUB→AZW3→EPUB is checked for retained text.

`test-performance.ps1 -WorkerPath ...` measures one first capability query and one
small 640×480 PNG→JPG conversion, including a sampled worker peak working set. It
is a narrow local probe, not an overall performance promise or a child-engine total.

`test-components.ps1 -WorkerPath ...` checks the management protocol and real
cross-process leases without network traffic. A corrupt or unowned optional
component store must not block built-in conversions: those run without managed
executable paths and report a limitation. Busy leases still fail closed, and
component-management operations remain strict; fallback never adopts or repairs
unrecognized storage. `test-component-roundtrip.ps1`
explicitly downloads the pinned small 7-Zip package into a fresh workspace store,
verifies actual archive conversion, then uninstalls only that component while
checking that source and result files remain unchanged.

`test-media-component-roundtrip.ps1` explicitly downloads the pinned FFmpeg
package into its own workspace store, then checks actual MP4→WebM, MP4→MP3 and
MP3→FLAC through the public worker protocol. It verifies streams/duration,
idempotent update, uninstall, original/result hashes, and no system-engine
rediscovery after removal. It needs network access; a missing or timed-out download
is a failed/incomplete integration test, never proof that conversion passed.
