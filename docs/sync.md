# PCasa – Synchronization Model

## General rule

Synchronization exchanges catalog information and thumbnails between clients through an optional server-side synchronization database/storage.

Original images are not distributed by the synchronization layer.

Only explicitly selected network/server folders participate in central synchronization.

Local folders never participate in central synchronization.

## Server database availability

Central synchronization is optional.

If no server database is configured on a client:

- PCasa remains fully usable locally
- no server-synchronization controls are shown in the Folder Manager
- no folder participates in central synchronization

If a server database is configured:

- server-synchronization controls become visible in the Folder Manager
- local folders remain ineligible and their server-synchronization control is disabled/greyed out
- network/server folders can be selected individually for central synchronization

## Folder synchronization scope

The Folder Manager controls permanent monitoring and, separately, central synchronization eligibility.

For each monitored folder:

- Local folder:
  - permanently monitorable
  - central server synchronization is not allowed
  - when server synchronization controls are visible, the control is disabled and greyed out
- Network/server folder:
  - permanently monitorable
  - central server synchronization can be enabled or disabled explicitly for that folder
  - being a network/server folder alone does not make it part of central synchronization

Therefore, the central server database contains only catalog data and thumbnails belonging to network/server folders that were explicitly selected for server synchronization.

## Path normalization

PCasa stores complete paths. Paths are not split into a root plus a relative path.

For network sources, the canonical synchronized path is always the complete UNC path.

Drive letters and mapped network drives are client-local conveniences only and must never become the canonical synchronized identity.

Example for the same image:

- Client A local access path: `P:\Fotos\2024\IMG_0001.jpg`
- Client B local access path: `X:\2024\IMG_0001.jpg`
- Canonical synchronized path: `\\PHOTO-SERVER\Photos\2024\IMG_0001.jpg`

The catalog stores the complete local access path needed by the client and the complete canonical path needed for identity/synchronization.

Changing a client drive mapping must not alter the canonical synchronized path.

## Manual synchronization

In addition to automatic monitoring, the user can explicitly synchronize:

- the selected folder
- the selected folder including subfolders

The folder manager itself is only for permanent monitoring configuration and for choosing whether an eligible network/server folder participates in central synchronization.

## Identity

No artificial image UUID is defined as the primary truth at this stage.

The complete canonical path identifies the file in the catalog. Renames or moves performed by PCasa can update the catalog directly. External renames/moves may later be correlated using file attributes or fingerprints where useful.
