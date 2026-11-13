# Frozen R3 library core remainder

This is an external compatibility supplement for the exact 0.1.45 R3 product
SHA-256 `97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6`.
It does not change product source or overwrite the original core tests.

The original VM run **865 remains failed** at the obsolete sidebar footer
geometry assertion. The external run **885 passed 7,849 assertions**, with
both runner and suite exit codes zero, no watchdog firing, and no remaining
Job processes. This is not a claim that the original `Mori.Tests.Run` passed.

## Scope and adaptations

The copied `R3CoreRemainderTests.Run` starts at `SidebarSpacingTests`, executes
all 14 remaining test groups in the original order, and retains the complete
original inline library import, metadata, export, backup/restore, migration,
web bridge, rollback and reopen checks. `Check`, `Throws`, `Draw`, `Json`,
fixture setup and failure/report handling are retained.

- Sidebar spacing now accounts for the separate collection and bottom utility
  groups. The original category height (>220 px) and trash target (>=34 px)
  requirements remain. Actual visible targets must fit, accept hit tests,
  support keyboard focus and not overlap. XAML is loaded from the pinned
  product assembly, not from the external helper.
- Batch geometry now measures each visible command instead of the collapsed
  compatibility delete button. It retains all original mutation checks and
  adds actual bounds, hit testing, focus, separation and the real delete menu.
- Navigation now verifies the actual grouped sidebar and invokes every visible
  type-menu route. Original format classification and compatibility routing
  checks are retained.

All other groups invoke the original methods in the frozen product. Some old
inspector button assertions in `TrashActionTests` and `BookmarkPageTests` check
compatibility state under a hidden parent; they are **not** visibility evidence.
Visible deletion is checked through the real context menu and selection menu.
Original thresholds were not lowered to accommodate VM screen size.

## Reproduction

Use Python 3.11+ on Windows with .NET Framework's x64 compiler. Compilation is
safe on the development host; **execute the helper only in the dedicated test
VM**. The wrapper enforces QEMU/NookTester and an owned hash-addressed package,
isolates settings/temp/fixtures, verifies the product hash, gates startup until
Job containment is active, and has a 600-second plan timeout. It retains test
evidence and does not clean user data.

```powershell
python tests/compatibility/r3-core-remainder/build.py --build <frozen-release-build> --output <fresh-output-directory>
```

The helper compiles with assembly identity `QingJie.Tests` for friend access,
then uses the filename `R3CoreRemainderRunner.exe`; do not replace the independent
capture test binary. The wrapper's `Target` intentionally points to the reviewed
full R3 VM payload `C:\NookLab\Audit\apps\72F61B078B2C`. This is not a standalone
package without that payload and its complete dependencies.

To rerun, construct a fresh hash-addressed test package with the two freshly
compiled helper executables, the product runtime config, and a reviewed
`plans/core-remainder.json`. Set the plan executable to
`R3CoreRemainderRunner.exe`, its newly observed SHA-256, arguments `["$root"]`,
timeout `600`, and the exact success pattern used by the supplied wrapper.
Then invoke `CoreRemainderGuest.exe --run core-remainder` through the VM test
controller. A rebuilt helper has a new binary identity and must be reviewed;
do not relabel an old receipt as a new run.

The wrapper stores all check lines in evidence chunks listed in `coreEvidence`.
The original full report is 701,821 bytes (UTF-8 with BOM), SHA-256
`9CEAB597B72E009D63E26BC27907C15CE7FAFD1561155C3A9FAD69184246F165`.
The verified original package SHA-256 is
`C82295FAD009C877D8A75FD04881E87EE4057CE0535CEE1F5B3FAA5F412B95E8`.
Source hashes and the compact result receipt are included next to this file.
