# PCasa Taskmanager / Scheduler contract

This is the acceptance contract for the existing scheduler. Do **not** redesign
the worker lanes as part of a UI clean-up. Slow NAS I/O, manual original-file
writes and catalog reads need their existing bounded isolation.

## One owner for execution

- **PCasaScheduler** is the only execution, yielding and global-order owner.
  No Taskmanager queue, worker, or independent UI priority list exists.
- **SchedulerTaskView** is the scheduler's read-only projection. It combines
  persistent job receipts and the existing scan owners' accepted intents.
  It does **not** create execution work. The Taskmanager filters its result.
- Durable jobs retain the original `scheduler_packages.package_key`,
  recipe version, retry/deferral state and completed-step cursor across
  restart. Existing `gps-edit-v1` remains restorable alongside v2.
- Automatic scanner tasks retain their `automatic:<source>:<kind>:<path>`
  identity while awaiting the single background scan owner. During actual
  execution, `background-scan:...` is an alias of that same intent.
- One logical task must have one visible row. An automatic **ScanFile** is
  **"Datei einlesen / Import file"**, not a GPS writer. GPS writing is a
  `gps-edit-v1/v2` package named **"GPS schreiben / Write GPS"**.

## Admission and responsiveness

- A Filmstrip/Gallery GPS action writes its immutable v2 intent to the local
  journal *before* eligibility checks and original-file edits. The accepted
  key is traceable via `GPS.Edit.Admission` and `GPS.Edit.Accepted`.
  Rejected UI commands are traceable via `GPS.Filmstrip.Rejected`.
- Admission notification and user reordering are immediate control-plane
  changes, not queued behind the work they intend to control.
- A manually moved automatic **ScanFile** must inform its original scan
  owner, just like a full-root review; a pending/in-flight scan is requeued
  with its existing confirmed cursor at its safe cancellation boundary.
- Frequent progress messages must not force repeated full SQLite projections.
  Accepted tasks and user order changes still trigger immediate refresh.

## Non-regression invariants

1. Original P0 manual write tasks retain the **LIFO** default; background
   work does not overtake them when there is no user-defined order.
2. User-defined order survives restart and is enforced at the existing
   scheduler's safe execution checkpoints. The Taskmanager shows the same
   effective order.
3. A slow/offline NAS scan does not suppress unrelated local P0 operations.
4. Original-file writes are never canceled halfway through an atomic
   file-write/metadata-verify step. A scheduler yield is not a delete.
5. An unavailable source or canceled scan does not imply removal of photos,
   thumbnails or confirmed scan cursors.
6. No journal migration silently deletes uncommitted GPS/Tag writes.

## Tests and diagnostics

The Windows acceptance workflow runs catalog regression tests,
`--taskmanager` and `--taskmanager-global`. The latter now also protects
default P0 LIFO, background isolation, a blocked GPS factory, scheduler
order persistence and distinct GPS vs ScanFile task identities.

On a user's NAS/VPN machine, correlate the timestamped
`GPS.Filmstrip.Requested`, `GPS.Edit.Admission` and
`GPS.Edit.Accepted` messages with scheduler P0 state and the displayed
task key. A screenshot of "Import file" alone is **not** evidence that
the GPS write was renamed. Do not guess without these data.
