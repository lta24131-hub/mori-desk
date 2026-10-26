# Shell registration fixtures

These tests are compiled on the development host and **executed only in the development VM**. They do not register a real shell component, invoke Explorer, or read live association keys. Both simulated HKCU and HKLM are disposable children of a fresh `HKCU\Software\Nook.Tests\ShellVisuals-<guid>` key. File fixtures remain under the supplied output directory for inspection. The test creates a directory junction fixture and never follows or recursively deletes it.

Compile only:

```powershell
./tests/shell-visual-registration/run.ps1 -OutputDirectory D:\Nook-Audit\ShellRegistrationBuild
```

In the VM, run the compiled `Mori.exe` with `--isolated-fixtures` and a fresh output directory, or invoke the script there with `-RunInVirtualMachine`. Keep the output root short (for example `C:\NookLab\Audit\data\sv-1234abcd`) so test scaffolding does not consume Windows' legacy path budget. Individual cases use short numbered folders, with scenario names in `scenario.txt`. A separate test deliberately exercises long component paths and requires unchanged associations after failure. The test executable has Nook metadata only so that the real committed-install verification path can be exercised. Its DLL, renderer and icon fixtures are deliberately plain text and are never loaded or executed.

`--generic-image-fixtures <fresh output directory>` runs only the Windows generic-image regression cases using the same compiled production registration code. It verifies that the confirmed machine-level Windows Photo Thumbnail Provider does not suppress SVG registration, while unknown generic providers, HKCU overrides, custom DLLs, redirected COM classes and dedicated SVG/ProgID associations remain protected. Both entry points refuse fixture writes unless running as NookTester in the dedicated QEMU/KVM/Bochs VM. The full `--isolated-fixtures` suite includes these cases.
Coverage includes exact inventories and hashes, path escape and junction rejection, active installation checks, immutable component publication, preservation of protected defaults and existing thumbnail providers, effective machine/user and ProgID/CurVer providers, per-value fault injection and compare-and-restore rollback, idempotence, ownership guarded removal, and preservation of potentially loaded DLL folders.

Real native/Explorer acceptance is a separate VM test: use `IShellItemImageFactory` with `SIIGBF_THUMBNAILONLY` to prove SVG pixels are actual thumbnails; also check PNG/JPEG/video provider preservation, Open With branding, and icon fallback at small sizes.

Payload schema (`shell-preview/manifest.json`, UTF-8 without BOM):

```json
{
  "schemaVersion": 1,
  "componentVersion": "1",
  "files": [
    {"path": "Nook.ShellPreview.dll", "length": 1234, "sha256": "64 lowercase hex characters"}
  ]
}
```

Every payload file except the manifest must be listed exactly once. Paths use `/`, are relative, and cannot contain aliases or Windows device names. Required entries are `Nook.ShellPreview.dll`, `resvg.exe`, `icons/app.ico` and `icons/file.ico`. Include all license/notice files and format icons in the same inventory. The SHA-256 of the exact manifest bytes names the immutable `Launcher\Shell\<hash>` folder. No symlinks, junctions, extra files or empty extra directories are accepted.
