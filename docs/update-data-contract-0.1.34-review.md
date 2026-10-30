# Nook 0.1.34 data-contract review

Reviewed on 2026-10-30. Contract: `nook-c33-data-v1`; baseline: `0.1.33.0`.
The five changed source pins were reviewed manually. Packaging validation is
still mandatory; the contract has no automatic hash-refresh or bypass step.

| Reviewed source | Finding |
| --- | --- |
| `modules/library/src/Library.cs` | Backup now uses a unique sibling staging file, exclusive creation, durable flush, and ownership-aware cleanup. Database schema and ZIP entry layout are unchanged. |
| `modules/library/src/Settings.Customization.cs` | Adds persisted Boolean `IncludePreviewUpdates`, defaulting to `true`. The rollback limitation below is explicitly accepted. |
| `modules/capture/src/PinStore.cs` | History loading uses an ordinal HashSet and pin actions move to a context menu. Persisted pin fields, ordering, first-duplicate handling, and serialization format are unchanged. |
| `modules/library/src/Library.ThumbnailRecovery.cs` | Completed PDF/AI/document thumbnails with unknown original dimensions may use the read-only warm-cache path. Source-stamp and cache validation remain in place. |
| `modules/library/src/ThumbnailImage.cs` | Visibility, cancellation and resume behavior change; no persisted representation changes. |

The prior pinned bytes for all five files matched their Git HEAD blobs before
review. No other source pins were refreshed for this review.

The compatibility run used the real release assemblies in four separate VM
processes and fresh synthetic data. All four phases passed:

| Phase | Assembly | Verified behavior |
| --- | --- | --- |
| Seed | 0.1.33.0 | Creates synthetic library, categories, originals, nondefault data routes, settings, and active/closed pins. |
| Forward | 0.1.34.0 | Reads all old data unchanged, saves the new setting, edits metadata/pins, adds an image/category, and creates a backup. |
| Rollback | 0.1.33.0 | Reads candidate data, saves further edits, and restores the candidate backup into a separate empty directory. |
| Re-upgrade | 0.1.34.0 | Preserves rollback edits, both images/categories, pin fields and active/closed state, original hashes and data routes. |

Checks compare every fixture database table's schema, columns, storage types
and values, verify SQLite integrity and foreign keys, hash managed originals
and pin PNGs, compare every persisted pin field, and compare all original
settings. The new backup was successfully restored by the old assembly with
matching schema, values and original-file hashes.

**Accepted rollback limitation:** when 0.1.33 saves settings, it drops the
unknown `IncludePreviewUpdates` key. Re-upgrading therefore restores its
default `true`; a previous choice to exclude preview updates must be set again.
This is the only accepted setting difference. Existing settings, library and
conversion paths, images, classifications and pins must remain preserved.

This is **model-API compatibility evidence**, not a full application launch,
installer rollback, browser, desktop interaction, or complete feature test.
No product entry point or window was launched. The baseline is explicitly
0.1.33 because that is the old assembly tested. This review does not claim
0.1.26 rollback validation. The `same-reviewed-data-contract-only` policy
remains authoritative; these results do not bypass installer eligibility.

| Evidence artifact | SHA-256 |
| --- | --- |
| Old `Mori.exe`, 0.1.33.0 | `327E7EA7089A3504F0BCFF8242FD296A7809BE709074049525F9A04C2143C3CC` |
| Candidate `Mori.exe`, 0.1.34.0 | `296BDA22F5F263FB31DE29AF6C1C4DDAF1F1940EFF85D5A473164368E9C3C5C5` |
| `DataContract34Tests.exe` | `92407E8738052E1396430D3F1C6E2479A6B16A304341F3BE57428FBA71165A87` |
| `DataContract34Guest02.cs` | `61E7853BBCE15134859FD9940824537B85228A592186D6904BC4358578CBC67E` |
| `compatibility02.zip` | `A81D1D2A6F18D8FCCB15708091940F7E9C16FB449697162C515E9544463E4029` |
| Guest report `audit-data-contract34.json` | `E70D9955C6F6F743E8881D70A5DBA5544F28669A02A874D9014051156A565F40` |
| Transport evidence `latest-release34-contract02-run.json` | `AB51A933FF807803CD1A4973523F418AD5FD6B5849D1A65054E39AA2C13366A4` |
| Reviewed `update-data-contract.json` | `111D18F6A6BFE3E37424A6FCB92A011CB61A8722464623A640DBA7832AD62323` |

Raw VM reports, preparation receipts, harness source and queue scripts remain
in the private release audit archive. They are not part of the product package.
