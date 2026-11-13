# R4 installation-maintenance native regressions

These are original source snapshots, compiler recipes, package audits and returned dedicated-VM reports for R4 (`A26E4E47…`). Task 1351 passed 102 assertions in the original embedded maintenance/artifact suites; task 1353 passed 420 installer-safety assertions. All test children exited, both watchdogs remained inactive, and fixture data was retained. These are 522 native assertions across three named suites, not 522 user journeys.

`sources/installer/InstalledVersionCleanup.cs` and `sources/src/InstalledVersionMaintenance.cs` preserve the exact original R4 product bytes. Their hashes, BOM and newline counts are in `source-provenance.json`. They are archival copies and must not replace current production source implicitly. Other frozen source snapshots include the original suites and all standalone installer-safety inputs.

`build.py --build <exact frozen R4 release-build> --output <fresh external directory>` is compile-only. It verifies the frozen product, source, compiler and framework-reference hashes, then rebuilds StageGuest, ExternalGuest and InstallerSafetyTests. It does not rebuild Mori.exe or execute any test. Rebuilt executables require new reviewed hashes and VM deployment plans. The historical package recipe is retained for evidence; it is not a ready-to-replay controller.

The raw broker reports are authoritative. The local installer stdout copy differs only by Windows LF-to-CRLF expansion, as the independent result review records. Maintenance stdout/stderr are missing-file markers because its assertions are in the complete `progressTail`, not missing test results or timeouts.

These native passes do not qualify an actual upgrade, browser collection, physical desktop acceptance or a changed binary. Original R3 task 1304 remains failed. The actual public42-to-R4 upgrade evidence is separate. No binaries, VM credentials, broker configuration, product changes or test execution are included in this archive operation.
