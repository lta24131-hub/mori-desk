# Nook Windows file visuals

`Nook.ShellPreview.dll` contains two independent native COM classes:

- `{612E3A5A-3C2C-4709-8C54-9C3EA927C086}` implements `IExtractIconW` and `IPersistFile`. It selects a fixed `icons/EXT.ico` resource by filename only. `icons/file.ico` is the document fallback; an empty filename or EXE uses `icons/app.ico`. It does not parse documents, render thumbnails, launch Nook, read a library, or access a network.
- `{FA45A458-130E-47BA-AAE0-1202D6930A79}` implements `IInitializeWithStream` and `IThumbnailProvider` for SVG. It uses Windows XmlLite to produce a safe SVG stream, runs the pinned sibling `resvg.exe` as an isolated worker, and decodes its PNG with Windows WIC. Shell should use its normal isolated thumbnail surrogate. Do not set `DisableProcessIsolation`.

Both classes require `ThreadingModel=Apartment`. Registration is external and must remain per-user, avoid other applications' handlers, and never edit a protected Windows `UserChoice`/Hash. The DLL deliberately does not expose self-registration exports. Keep this DLL, renderer and ICO files in an immutable application-owned component directory; an Explorer-loaded icon DLL must not block deletion of an old main-program payload.

SVG input is limited to 8 MiB, XML depth 96, 100,000 nodes and 256 attributes per node. DTDs are prohibited. The root must be SVG, foreign namespaces and active/embedded-resource elements are rejected, and processing instructions are omitted. Attribute entities are decoded before resource checks. External `href`, CSS imports/escapes/comment tokens and non-fragment `url(...)` are rejected. Safety-subset failures return an HRESULT and permit the format icon fallback. This initial implementation also rejects inline bitmap `image`/`feImage` elements rather than risking oversized embedded images. Safe CSS classes, paths, text, gradients, masks and fragment references are retained.

The renderer is launched directly by full sibling path, with explicit inherited handle allowlisting, hidden and suspended until assigned to a job. Its job limits memory to 256 MiB and active processes to one, and kills the worker when the job closes. Execution is limited to 3 seconds for ordinary vectors; only sanitized SVGs containing actual `text` elements receive up to 8 seconds because resvg then initializes its system font database. The first text thumbnail can therefore be slower; Shell's normal thumbnail cache avoids repeating the work for unchanged files. Timeouts return failure for an icon fallback. Output is limited to 8 MiB and thumbnail sides to 1,024 pixels. A per-user low-integrity-compatible semaphore permits at most two renderer jobs. Input and output use asynchronous, local-only named pipes with a current-user-only DACL. No temporary document, global icon-cache deletion, Explorer termination, UI or helper thread is involved.

Rendering failures must not be mistaken for success merely because Shell returned an icon. VM verification must query `IShellItemImageFactory` with `SIIGBF_THUMBNAILONLY`, check an actual SVG's visual result, and verify that existing PNG/JPG/video providers and default choices remain unchanged. Verify Open-with's application logo separately from its document icons.

The wrapper is Nook code. The renderer must ship with its pinned upstream metadata and original resvg licenses. Relevant platform references:

- https://learn.microsoft.com/en-us/windows/win32/shell/how-to-create-icon-handlers
- https://learn.microsoft.com/en-us/windows/win32/shell/thumbnail-providers
- https://learn.microsoft.com/en-us/windows/win32/shell/building-thumbnail-handlers
