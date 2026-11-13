Nook custom-location launcher, protocol 1

Used only for installations created in a custom program folder. The established
default-location launcher remains unchanged. Reuse this binary for releases;
do not rebuild it during routine packaging.

The source/compiler hashes are recorded in launcher-provenance.json.
Validated in the isolated NookTester QEMU guest on 2026-11-11:
- Program folder containing spaces, resolved from the installation location.
- File argument containing spaces delivered unchanged.
- The same launcher follows the replacement's committed active descriptor.
- Native messages preserve bytes and reply before input reaches EOF.

VM evidence: 368-custom-launcher, exit 0. No host installation was modified.
