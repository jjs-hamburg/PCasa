# PCasa – Database Principles

## Local database

- SQLite.
- Complete local working catalog.
- Must allow PCasa to operate without any server.

## Optional server database

- Synchronization/distribution point only.
- Not authoritative over the file system.
- Does not scan image folders.
- Exact database engine is not fixed yet.

## Initial synchronized data categories

Expected to include at least:

- canonical source/path information
- relevant EXIF/photo metadata
- tags
- GPS metadata
- edit/backup state required for UI indication
- thumbnail references/data
- synchronization state

The exact schema will be designed only after synchronization semantics and conflict handling are specified.

## Client-local data

Expected to remain local where appropriate, for example:

- window geometry
- current UI layout
- local drive mappings
- local cache state
- current folder/scroll position
