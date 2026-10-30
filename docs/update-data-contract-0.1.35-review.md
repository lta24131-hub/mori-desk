# Nook 0.1.35 data-contract review

Reviewed on 2026-10-30 against `nook-c33-data-v1`, baseline 0.1.33.0.
The preceding 0.1.34 review remains applicable. Two additional source pins
were reviewed manually; no automated pin refresh or validator bypass is used.

| Source | Review |
| --- | --- |
| `modules/library/src/Program.cs` | Adds early dispatch for `--pdf-render-worker`. No change to data paths, persisted formats or model startup. Reviewed SHA-256: `d470590db1c2a5626797efd8fe9879c0546034d8c53de81af5eab9f184969ef4`. |
| `src/MatureCanvasStore.cs` | The bounded PNG encoder returns its actual width/height to the import caller, eliminating a second decode. The same PNG encoder, resource registration, MIME type, naming, validation and storage schema remain. Reviewed SHA-256: `88ff35b9273016ac4e6de6e1a1801da0a1804f0e0fc4e4006bd3b4e8944b8753`. |

The first .35 candidate (`FCB6EC6970628EB293C07017C509B0430A5A0F7A48CA9E172B7C58DB0BD4FC7E`)
passed the real assembly model sequence .33 → .35 → .33 → .35 in the dedicated
VM: 198 seed, 253 forward, 253 rollback, and 227 re-upgrade checks. Records,
schema, categories, original-file hashes, pins and non-default data routes were
preserved. Evidence: private `20261030-release35/reports/latest-release35-contract.json`.
The later PDF toolbar/navigation changes do not modify any pinned data source.
Final-package verification is recorded separately with its exact executable hash.

This is model API evidence, not installer rollback or whole-app acceptance.
The previously accepted limitation remains: an old .33 settings save drops the
new `IncludePreviewUpdates` field; re-upgrade restores its default true.
All other existing settings and material must remain preserved.
