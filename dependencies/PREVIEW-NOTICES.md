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

## Embedded web-media regression fixtures (BSD-3-Clause)

The test assembly contains the real short videos `media/2x2-green.mp4`
(3503 bytes) and `media/2x2-green.webm` (814 bytes), unmodified, from:
https://github.com/web-platform-tests/wpt/tree/10dd48f8bc0c18d6b695c83dfa230880385dbf79

Copyright © web-platform-tests contributors

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
