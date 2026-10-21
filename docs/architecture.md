# PCasa – Architecture Principles

## Local-first design

Every PCasa client must remain fully usable without a server connection.

Each client maintains:

- a local SQLite catalog
- a local thumbnail cache
- local UI/client settings
- direct access to configured photo folders

## File system as source of truth

The file system determines whether a photo exists and where it is located.

The database is a catalog, cache and synchronization aid. It is not authoritative over the file system.

Original image files remain ordinary files in ordinary folders.

## Optional server synchronization

A server database is optional.

Its role is limited to synchronization and distribution of catalog data between clients.

The server:

- does not scan photo folders
- does not act as a central file indexer
- does not own the photo collection
- does not replace the local client database

## Thumbnails

Thumbnails are synchronized so that a new client does not need to regenerate hundreds of thousands of previews from the originals.

Each client may retain a local thumbnail cache for fast browsing.

## Framework status

The desktop UI framework and imaging stack are intentionally not fixed yet. They will be selected after the requirements and performance-critical paths are agreed.
