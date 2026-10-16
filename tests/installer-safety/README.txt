Installer safety checks
=======================

Run run.ps1 to compile Setup.cs with a test-only entry point and execute pure
identity, path, request-protocol, startup-command and graceful-wait checks.
This does not run the installer entry point, inspect running processes, write
the registry, create shortcuts, launch Nook, extract the payload, or touch a
personal library. The optional SuiteExecutable argument reads file metadata.
Completion-marker fixtures are generated only beneath a fresh GUID temporary
directory, whose location is printed and retained. No existing data is removed.

Authorized update contract
--------------------------
Nook starts the downloaded/checksummed Nook-Setup.exe without a shell:
  --update-request "<local request.json>"

The request must be a new local file at:
  %LOCALAPPDATA%\MoriDesk\updates\<32 hexadecimal session characters>\request.json

Exact JSON fields:
  schemaVersion: 1
  parentProcessId: integer process ID
  parentExecutable: absolute installed ...\Programs\MoriDesk\...\Mori.exe
  parentStartedUtcTicks: integer Process.StartTime.ToUniversalTime().Ticks
  expectedVersion: exact three-part embedded installer version, e.g. 0.1.24
  relaunch: true

The installer rejects other fields, arbitrary launch arguments, URLs, UNC
paths, alternate data streams, links/reparse points, stale sessions, wrong
version/product/session, and reused process identities. It verifies the live
parent and stages the bundled program side by side before publishing ready.
No old program directory or user data directory is removed.

Nook must remain running until ready.json appears. Payload extraction can take
longer than 30 seconds; the client should allow a longer preparation timeout.
ready.json and result.json contain schemaVersion, status, expectedVersion and
message. Ready status is "ready". Terminal statuses are "installed",
"cancelled", or "error". Result files are atomically created, not overwritten.

After ready, Nook uses its existing normal exit path, including pending metadata
choices, canvas flush and persisted pins. The installer waits at most 30 seconds
on the original process handle; it never kills a process. If a window vetoes
closing or the user cancels, Nook writes cancel.request in that session. No
shortcut or startup-registration change occurs unless the parent has exited and
no other same-session Nook process remains. After process exit, the installer
also requires the exact session-local metadata-backup/complete.json. This file
is bounded to 4096 bytes, rejects reparse-point components, and must contain
the exact schema/UTC-time/metadata-only/excluded-media/excluded-credentials/
bounded-byte-count fields. Missing or invalid completion, or cancellation
arriving together with process exit, cancels installation rather than silently
skipping the backup. The normal close handler writes this marker last.

Startup confirmation
--------------------
Only the freshly launched new child receives NOOK_UPDATE_REQUEST and
NOOK_UPDATE_EXPECTED_VERSION in its private process environment. The app clears
these variables when consuming them. At the successful real library main
window's ContentRendered, it verifies the fixed GUID session, original request,
ready state, installed executable identity, expected version and no cancellation.
It then atomically creates session/startup-ready.json with exactly:
  schemaVersion, expectedVersion, processId, startedUtcTicks, executable

The installer waits at most 30 seconds for this receipt and checks it against
the actual launched process, executable and start time. Process survival alone,
a startup-error message box, a stale receipt, another PID or an exiting child
cannot mark an update installed. Failed startup attempts normal window close
only for the exact owned child; no process is force-killed. If that window
refuses to close, the user is told and another old version is not started on
top of it. Old shortcuts/startup/open-with entries are conditionally restored.

Only an exact existing own HKCU Run command is migrated. Disabled and unrelated
startup entries are unchanged. Existing owned shortcut bytes are retained for
failure rollback; unrelated shortcuts are never overwritten. Failed installation
after parent exit attempts to reopen only the originally verified old program,
with its original SHA-256 still matching and no arguments. Old program files,
pins, settings and libraries remain in place.

Limits of these tests
----------------------
The pure checks do not prove OS shortcut/registry behavior, full installer
extraction, real graceful window shutdown, or successful GUI startup. The
main-window startup receipt confirms initial readiness, not all later features.
Future schema-changing releases also need explicit data-migration backups;
retaining an old executable alone does not undo a database migration.
