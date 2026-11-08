# PCasa Windows Setup (0.1.Alpha.2)

The Windows acceptance workflow publishes both the existing unpack-and-run
`PCasa-win-x64` package and `PCasa-Setup-0.1.Alpha.2.exe`.

The installer offers **Only for me** (no administrator rights, under the
current user's application directory) and **All users** (requires
administrator rights, program files under Program Files).

In All-users mode PCasa uses `%ProgramData%\PCasa` for the SQLite catalog,
durable scheduler journal, and both thumbnail caches. In Only-for-me and
portable ZIP mode, the old location `%LocalAppData%\PCasa` is unchanged.
Display options and other per-user preferences always remain in the
individual user's `%LocalAppData%\PCasa`.

The installer's all-users marker is deployed only in machine-wide mode.
A cross-session mutex prevents two interactive PCasa instances from
opening the common database simultaneously. The separate offline database
restore/optimize process is explicitly exempted from that mutex, because it
must coexist briefly with the exiting parent.

## Backup and restore

PCasa's existing **Options → Database management → Backup/Restore**
uses the same single catalog root in both installer modes. To transfer an
existing per-user database to an all-users installation, first make a
`.pcasa.zip` backup in the original installation, then restore it using
the installed application in all-users mode. PCasa will validate the
archive, request folder remapping, and restart for offline restore.

Do not copy a running SQLite WAL directory manually. Do not move or
delete user data on uninstall. The installer installs application binaries,
not photographs, databases, or user preferences. When switching install
scope, back up the database first and uninstall the old scope separately.

## Release checks

Help → Check for Update checks published GitHub Releases only and opens
the release page after confirmation. Updating is user-controlled; no
automatic installation is performed.
