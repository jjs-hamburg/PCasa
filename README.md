# PCasa

PCasa is a fast, local-first desktop photo manager inspired by the straightforward workflow of Google Picasa, but designed for very large photo collections and optional synchronization between multiple clients.

## Core principles

- Local-first: every client works independently with its own local database.
- The file system is the source of truth.
- SQLite is used for the local catalog.
- An optional server database acts only as a synchronization and distribution point between clients.
- The server never scans photo folders itself.
- Network paths are normalized to canonical UNC paths for synchronization.
- Thumbnails and catalog metadata are synchronized between clients.
- Original photo files are not stored in the database.
- Target scale: at least 500,000 images.

## Initial UI concept

- Folder tree on the left.
- Thumbnail view on the right.
- Adjustable thumbnail size via slider.
- Modern, slim desktop UI.
- Image viewer with left/right navigation, fullscreen mode and slideshow.
- Clicking the displayed image returns directly to the thumbnail view while preserving folder, scroll position and selection.

## Status

Initial requirements and architecture phase. No framework decision has been locked in yet.

See [`docs/requirements.md`](docs/requirements.md), [`docs/architecture.md`](docs/architecture.md), [`docs/sync.md`](docs/sync.md), and [`docs/database.md`](docs/database.md).
