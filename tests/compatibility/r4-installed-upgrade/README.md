# R4 installed-upgrade journey

This archive preserves the exact helper sources, compile recipes, original preparation failure, dedicated-VM journey reports and independent reviews. The VM uses generated fixtures; the archive contains their identities and assertions, not media, databases, application binaries, installers, credentials or broker configuration.

`results/` retains the original immediate lease-read failure and premature release failure. `lease-failure-inspection/` records that the original lease became ready after about 9.4 seconds. The reviewed v2 controller observes readiness with a bounded wait and resumes the existing lease; it does not silently replay any product stage. `results-v2/` contains the actual baseline and upgrade results and both normal lease release receipts. Original R3 task 1304 remains failed and its state was separately preserved.

The final independent review referenced by `validation.json` defines the accepted result and limits. Healthy browser registration or model-level collection assertions must not be represented as an actual Chrome drag-and-save journey. This was not physical-desktop acceptance.

`build.py --build <exact frozen R4 release-build> --output <fresh external directory>` rebuilds only the archived test helpers. It verifies source and reference hashes and never runs helpers or the product. Rebuilt binaries require new reviewed hashes, fresh VM state and explicit scope review. Historical controllers, package hashes and commands are evidence, not authorization to replay installation, preservation moves or cleanup.
