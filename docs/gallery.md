# PCasa – Gallery and continuous folder scrolling

## Core behavior

The main gallery behaves like Picasa: photo folders form one continuous vertically scrollable stream.

A folder boundary does not end browsing. Scrolling continues directly into the next photo folder.

Each physical photo folder is shown as its own section with a compact section header followed by its thumbnails.

Example:

- Urlaub Italien
- Familie
- Garten

The user can scroll from the last thumbnails of `Urlaub Italien` directly into the `Familie` section and onward into `Garten` without changing views.

## Folder paths

PCasa always works with complete folder paths.

The Folder Manager stores each monitored folder as a complete path.

When PCasa discovers physical subfolders containing images, those photo folders are also stored with their complete paths.

For network folders, the canonical path is the complete UNC path. PCasa does not persist a separate root plus relative path model.

The monitored-folder configuration and the discovered photo folders are separate concepts, but both use complete paths.

## Folder tree interaction

- Clicking a photo folder in the left tree scrolls the continuous gallery to that folder section.
- Scrolling manually across a folder boundary updates the active folder indication in the left tree to the section currently entering the viewport.
- The gallery itself remains one continuous scroll surface.
- Expanding/collapsing the tree does not rebuild or rescan images.

## Gallery section layout

Each section contains:

- compact folder name/header
- optional image count
- thumbnail rows directly below it

There is no heavy frame around a section. Separation is achieved with spacing and a subtle divider/shadow consistent with the approved dark UI direction.

## Thumbnail appearance

- no permanent thumbnail frame
- light soft shadow
- selected thumbnail: thin, restrained accent border
- small corner markers may indicate GPS data or edited/restorable state
- thumbnails use cached previews; originals are not decoded merely for scrolling

## Virtualization and scale

The gallery must remain responsive with a catalog of at least approximately 500,000 images.

The UI must therefore virtualize vertically and must not create one WPF control for every catalog image.

Implementation direction:

- the continuous feed is represented as section headers plus thumbnail rows
- vertical row containers are virtualized
- image metadata and thumbnail references are read from SQLite in pages
- thumbnail bitmaps are loaded only for visible/near-visible rows
- moving the thumbnail-size slider repacks visible rows without rereading original image files
- scrolling does not trigger file-system access for already cataloged images

## Ordering

Initial ordering is by complete folder path and then by image order within each folder.

The exact image sort modes can be expanded later; the architecture must allow alternatives such as capture date, filename and file date without changing the continuous-folder model.

## Synchronization relationship

Server synchronization and gallery browsing remain separate concerns.

- Every client can browse its local catalog completely without a server.
- Only network folders explicitly enabled for central synchronization contribute catalog/thumbnail data to the optional server database.
- Local folders can still appear normally in the continuous gallery but are never synchronized through the server.
