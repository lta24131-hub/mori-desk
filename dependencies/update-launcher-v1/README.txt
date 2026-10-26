Nook stable application launcher, protocol 1

This small launcher is frozen independently of the application payload. The
installer reuses these bytes so the Windows shortcut, file association, and
browser native-host entry stay stable across application updates.

File version: 1.0.0.1
SHA256: 58f1d00650e3f7dd9eb93c7d382894bdddd6b339053152d8e1979a5dbc2e0895

Do not rebuild this executable during ordinary application packaging. Any new
launcher candidate needs a separate protocol and native-message regression in
an isolated VM. The build helper produces a candidate, not a validated replacement.
