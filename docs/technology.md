# PCasa – Initial Technology Decisions

Status: bootstrap baseline

## Platform and UI

- Windows desktop application.
- .NET 10 LTS.
- WPF as the UI framework.
- Built-in WPF Fluent theme in system mode for a modern, lightweight Windows appearance.
- x64 is the initial target architecture.

### Why WPF

PCasa is deliberately Windows-first. WPF is mature, actively maintained with current .NET releases, supports hardware-accelerated rendering, XAML/data binding, virtualization and modern Fluent theming. We therefore avoid the additional platform and abstraction cost of a cross-platform UI framework at this stage.

## Local catalog

- SQLite is the local client database.
- `Microsoft.Data.Sqlite` is used directly.
- No ORM is required for the initial implementation.
- SQL, indexes and transactions remain explicit because catalog performance at approximately 500,000 images is a primary requirement.

## Image processing

- libvips is the initial image-processing engine.
- .NET binding: NetVips.
- Native Windows x64 libvips package is bundled through NuGet.

libvips is intended for thumbnail generation, decoding, resize/crop, rotation/mirroring and the core pixel operations required by PCasa. Interactive editing will operate on screen-sized previews; the full-resolution source is processed only when the user saves.

## Thumbnail principle

- Thumbnails are files in a dedicated cache, not BLOBs in the SQLite catalog.
- The database stores catalog information and thumbnail cache keys/state.
- Thumbnail generation is asynchronous and must never block the UI.
- Only thumbnails needed for the visible/near-visible viewport are loaded into UI memory.
- Server synchronization will include thumbnail cache data so a new client does not have to regenerate the complete collection.

Exact thumbnail sizes and cache format remain open until they are benchmarked with a representative large photo set.

## Metadata

Metadata reading/writing is intentionally not fixed yet. Tag/GPS persistence must be decided separately because it affects interoperability, file modification behavior and server synchronization.

## Server synchronization

The optional server database remains a synchronization/distribution point only. It does not scan files and does not become the source of truth. The file system remains authoritative.

## Decisions deliberately postponed

- server database engine
- synchronization transport/protocol
- embedded metadata writer
- map component/provider
- thumbnail file format and size tiers
- installer/update mechanism
