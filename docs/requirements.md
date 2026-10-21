# PCasa – Minimal Requirements

## UI

- Modern, slim desktop UI.
- Main layout inspired by Picasa:
  - folder tree on the left
  - thumbnail view on the right
- Adjustable thumbnail size with a slider.
- Fast scrolling for very large collections.
- Multiple image selection.

## Folder Manager

- The folder manager only defines folders that are permanently monitored.
- Folders can be added to or removed from permanent monitoring.
- Server-synchronization controls are shown only when an optional central server database has actually been configured on the client.
- If no central server database is configured, the server-synchronization option is not displayed at all.
- When a central server database is configured, every monitored folder shows a separate server-synchronization option.
- For local folders, the server-synchronization option is disabled and visibly greyed out.
- Local folders are always local-only and must never be synchronized through the central server.
- For network/server folders, server synchronization is explicitly selectable per folder.
- Network/server folders are not synchronized automatically just because they are network folders; only folders explicitly selected for server synchronization participate in the central synchronization.
- Monitored folders are automatically checked for changes.
- Manual synchronization is a separate function outside the folder manager:
  - synchronize selected folder
  - synchronize selected folder including subfolders

## Tags

- Search field for tags.
- Tag editor.
- Add/remove tags for one image.
- Add/remove tags for multiple selected images.
- Optional right-side tag panel.

## GPS

- Optional right-side GPS panel.
- Map displaying the photo position.
- Display stored GPS coordinates.
- Small GPS marker on thumbnails when GPS data is present.

## Image Viewer

- Previous/next navigation with left/right.
- Fullscreen mode.
- Slideshow.
- Clicking the displayed image returns directly to the thumbnail view.
- Returning to thumbnails preserves:
  - current folder
  - scroll position
  - current selection

## Image Editing

Basic editing tools:

1. Crop.
2. Straighten / horizon correction.
3. Automatic adjustment ("I'm Feeling Lucky" style).
4. Contrast:
   - automatic
   - manual
5. Brightness:
   - automatic
   - manual
6. Retouch / clone.
7. Tone and color:
   - fill light / lighten
   - highlights
   - shadows
   - color temperature
   - select neutral color from image
8. Sharpen.
9. Blur / soften.
10. Rotate 90° left.
11. Rotate 90° right.
12. Mirror horizontally.
13. Mirror vertically.

### Editing behavior

- The original file is overwritten when edits are saved.
- Before the first overwrite, the original is copied to a central backup location.
- No hidden backup folders are created inside photo folders.
- Originals can be restored from the central backup location.
- Backups can be cleaned up on demand.
- A small thumbnail marker indicates that:
  - the image was modified
  - a restorable original backup exists
- Undo/redo is available during the current editing session.
- During editing, optional side-by-side comparison:
  - original
  - current edited state
- Both panes use the same zoom level and, where possible, the same image region.

## Performance target

- Designed for at least approximately 500,000 images.
- Clients must not have to re-scan the complete collection merely because another client already indexed it and synchronized the catalog.
