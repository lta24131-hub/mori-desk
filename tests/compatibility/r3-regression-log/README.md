# R3 regression complete-log supplement

Task 933 had runner exit code 0 but its external collector discarded stdout after 600,000 characters, so complete assertions and final summary were not retained. The original runner writes checks and summaries only to Console, not an independent test-report file; the missing tail cannot be recovered from the earlier run. Task 933 remains failed as an evidence-collection run.

The reviewed package preserves the original RegressionRunner.exe, source, plan, arguments and 300-second timeout byte-for-byte. Only the external wrapper now writes stdout and stderr to fresh owned files as each line arrives, with an 8 MiB limit per stream. Overflow or a log write error is still a failure. It preserves the complete files and hash-identified 64,000-character text parts without splitting a UTF-16 surrogate pair. Reconstruct every part and verify byte counts/hash before claiming a complete suite result.

`build.py --build <frozen-R3-release-build> --output <new-directory>` only compiles after checking the reviewed source and reference hashes. New binaries require fresh provenance, plan hashes, review and VM deployment; do not run on the physical computer or overwrite historical reports. Compiler success is not test success. `validation.json` records the returned supplement status.
