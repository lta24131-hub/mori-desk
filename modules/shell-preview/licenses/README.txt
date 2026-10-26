resvg 0.49.0 dependency license and copyright notices
====================================================

This directory accompanies the unmodified official resvg Windows executable.
The resvg MIT and Apache license texts are packaged alongside this directory's
dependency notices. resvg source: https://github.com/linebender/resvg/tree/v0.49.0

The dependencies/ directory preserves original license, notice, authors and
copyright texts from every registry package in the release's exact Cargo.lock.
This is a conservative superset: it includes build/development dependencies and
packages for other operating systems. Inclusion here does not mean a package is
linked into the Windows executable, and no corresponding third-party code is
modified by Nook.

dependency-notices.json records package versions, declared license expressions,
crate archive checksums, original source member names, and each notice's SHA-256.
Every downloaded crate archive was checked against its Cargo.lock checksum.
Original notice bytes are retained without normalizing whitespace or encoding.

Exceptions for old crates that omitted standalone license files:
- cloudabi 0.0.3: exact full BSD copyright/license comment blocks from the two
  checksum-verified source files are included with their source ranges recorded.
- phf_codegen, phf_generator and phf_shared 0.7.24: the shared MIT LICENSE is
  fetched from the exact source commit recorded in each crate's VCS metadata;
  both the source URL and content checksum are pinned.
- winapi GNU import-library subcrates 0.4.0: the original MIT/Apache texts from
  the same lockfile's checksum-verified winapi 0.3.9 parent release are included,
  together with each subcrate's original source copyright header.

The collector source is modules/shell-preview/licenses-source/CollectNotices.py
in the Nook source distribution. Its downloaded source archives are build-time
audit inputs and are not shipped in the application.
