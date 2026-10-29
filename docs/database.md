# PCasa – lokale Datenhaltung und Persistenz

**Stand 29.10.2026 · `scheduler-consistency`.** Das alte Dokument beschrieb lediglich ein noch zu entwerfendes Schema. Der aktuelle Client besitzt bereits ein konkretes SQLite-Schema und dauerhafte Wiederanlaufdaten.

## Autorität und Speicherorte

- **Originalbilder:** gewöhnliche Dateien in den ausgewählten Verzeichnissen; das Dateisystem ist die Wahrheit über Existenz und Pfad.
- **SQLite:** lokaler Katalog, Metadaten- und Tag-Index, Überwachungs-/Ausschlusszustände sowie bestätigte Scheduler-/Dateioperationen.
- **Lokale Vorschaudateien:** Einzel-Thumbnails in `%LOCALAPPDATA%\PCasa\thumbnails`; gepackte Browse-Thumbnails in `%LOCALAPPDATA%\PCasa\browse-cache`. Der SQLite-Katalog enthält Pack-Referenzen, aber die Bildpixel der Pakete liegen in Dateien.
- **Einstellungen:** `appearance.json` (u. a. Sprache, Theme, Sortierung, Ansichts-/Fensterzustand); `scan-settings.json` (automatischer Scan). Nicht versehentlich als überflüssige Dateien entfernen.

Die Datenbank wird im Quelltext von `Data/LocalDatabase.cs` initialisiert und migriert; die jeweilige Datenbankdatei und ihre aktuellen Versionen sind aus dieser Implementierung abzuleiten, nicht aus einem alten Planungsdokument.

## Wichtige SQLite-Objekte (Auswahl)

| Tabelle/Objekt | Zweck |
| --- | --- |
| `monitored_folders` | Überwachte Wurzeln und deren Konfiguration |
| `excluded_folders` | Ausgeschlossene Unterverzeichnisse |
| `photo_folders` | Erkannte Ordner mit Bildern |
| `images` | Bildidentität, Pfade, Größen und Zeitstempel |
| `image_exif` | EXIF, Kamera und GPS |
| `image_tags` und Tag-Suchindex/Trigger | Lokale Tag-Suche und Projektion der Stichwörter |
| `pending_tag_edits` | Dauerhaft vorgemerkte Änderungen an Originaldateien |
| `recent_tags` | Kürzlich verwendete Tags / Vorschläge |
| `thumbnail_entries` | Referenzen auf gepackte Bilddaten samt Quelle/Gültigkeit |
| `browse_cache_settings`, `browse_cache_jobs` | Thumbnail-Stufen und ausstehende Cachearbeiten |
| `scheduler_sources` | Dauerhafte Quell-/Generationsinformationen |
| `initial_scan_directories`, `initial_scan_file_cursors` | Fortschritt beim erstmaligen Durchlauf |
| `scheduler_packages` | Aufträge, Reihenfolge, Prioritäten, Schritt-Cursor und Retry-Zustand (erstellt durch `DurablePackageJournal`) |
| `file_transfer_intents` | Wiederherstellbare Dateiübertragungs-Intentionen |

Die Liste ist eine **Orientierung**, keine vollständige Schema-Spezifikation. SQL-Definitionen, Trigger, Fremdschlüssel, Indexe und Migrationen stehen in `LocalDatabase.cs` und den jeweiligen Repositories.

## Cache und Gültigkeit

Der normale Browse-Cache nutzt standardmäßig die Stufen **96 / 192 / 384 Pixel** (konfigurierbar über das Profil). `BrowseCacheRepository` führt Katalog- und Pack-Einträge zusammen; `PackedThumbnailReader` lädt lokale Pakete. Die Galerie verwendet `BrowseThumbnailPageCache`, sodass bereits dekodierte Bilder nicht bei jeder kleinen Änderung erneut eingelesen werden müssen.

Bei Tag-Schreibjobs kann die Fortschrittsanzeige auf den bereits vorhandenen **lokalen Einzel-Thumbnail** zurückgreifen, falls ein Pack-Eintrag fehlt. Die Originaldatei auf NAS/VPN wird hierfür nicht neu dekodiert.

## Tag-Schreiben und Wiederanlauf

1. Der Tag-Editor hält Änderungen über Auswahl- und Ordnerwechsel hinweg **pro Bild nur im Speicher**. Erst OK bestätigt die Sitzung; Abbruch verwirft sie.
2. OK erstellt **ein** dauerhaftes `tag-edit-v2`-Paket. Dessen unterschiedliche Änderungen je Bild werden mit **einer SQLite-Transaktion** im lokalen Tag-Index und in `pending_tag_edits` abgelegt. Bereits vorhandene `tag-edit-v1`-Pakete bleiben wiederherstellbar.
3. Der zentrale Scheduler schreibt die Original-Metadaten je Bild mit ExifTool; mehrere Änderungen desselben Bildes benötigen nicht mehrere Originaldatei-Transaktionen.
4. Nach der bestätigten Originaldatei-Transaktion wird `pending_tag_edits` abgeglichen; unerledigte Änderungen bleiben bestehen.
5. Fehlende/offline Quellen behalten ihre Intention. `scheduler_packages` bewahrt Priorität, Reihenfolge und bestätigten Cursor über Neustarts.
6. Ein reiner Keyword-Wechsel soll keine unnötige neue Pixeldekodierung auslösen. Der ExifTool-Schreibweg erhält den Dateizeitstempel und der Katalog gleicht notwendige Dateigrößenänderungen gezielt ab.

**Nie** eine scheinbar verwaiste Datei/Tag-Intention durch manuelles Löschen der Tabellen »reparieren«, ohne zuvor eine konsistente Migration und die referenzierten Originale zu prüfen.

## Mehrclient-Datenbank

Eine gemeinsame Serverdatenbank und die Verteilung der Thumbnails sind **noch nicht realisiert**. Die dafür gedachten Felder, z. B. `server_sync_enabled` in `monitored_folders`, stellen **keine** funktionierende Synchronisierung dar. Siehe [Synchronisierungskonzept](sync.md).
