# Lightweight preview components

This release bundles only Windows x64 image decoding and .NET Framework PDF
text extraction. No PDF.js, LibVLC, Ghostscript, Python, OCR enhancement models
or offline translation models are added to the base installation.

* Magick.NET Q8 x64 / Core 15.0.0 — Apache-2.0. See image-codecs/LICENSE.txt
  and image-codecs/Notice.txt for the native codecs' separate notices.
  The embedded synthetic HEIC self-test fixture has separate attribution and
  the upstream COPYING in image-codecs/test-fixtures-NOTICE.txt and
  image-codecs/test-fixtures-COPYING.txt.
  https://github.com/dlemstra/Magick.NET
* PdfPig 0.1.16 — Apache-2.0. See pdf-text/LICENSE.txt and NOTICES.txt.
  https://github.com/UglyToad/PdfPig
* Microsoft.Bcl.HashCode 6.0.0, System.Memory 4.6.0, System.Buffers 4.6.0,
  System.Numerics.Vectors 4.6.0, System.Runtime.CompilerServices.Unsafe 6.1.0
  — MIT; see preview-support/LICENSE.txt and the bundled NuGet manifests.
  https://github.com/dotnet/runtime

Restore-PreviewComponents.ps1 pins official NuGet package SHA-256 checksums.
Only the chosen framework DLLs and Windows x64 native image DLL are deployed.
The development packages are not the end-user installation size.
