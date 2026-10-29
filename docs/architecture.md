# PCasa – aktuelle Architektur

**Stand 29.10.2026 · Implementierung im Branch `scheduler-consistency`.** Diese Datei beschreibt die tatsächlich vorhandene Anwendung. Der ältere [Architekturbericht zu PR #14](architecture-review-pr14.md) und der [Scheduler-Entwurf](operation-scheduler-design.md) sind zusätzlich als Entstehungs- und Prüfhistorie zu lesen.

## Architekturregeln

1. **Eine Aufgabe – eine Zuständigkeit – ein Codepfad.** Kein paralleler Scheduler, keine unabhängige Tag-/Transfer-Warteschlange und keine zweite Galerie.
2. **Analyse vor Implementierung.** Bestehende Abläufe, Zustandsübergänge, Abbruch-/Wiederanlaufgrenzen und Dateisystemfolgen vollständig prüfen; Tests bestätigen die Architektur und ersetzen die Analyse nicht.
3. **Dateisystem ist Wahrheit über Dateien.** SQLite, Metadatenindex, lokale Thumbnails und Pack-Dateien sind abgeleitete bzw. zur sicheren Fortsetzung benötigte Zustände.
4. **Manuelle Aktionen priorisieren.** Ältere Arbeiten werden an sicheren Grenzen unterbrochen und mit ihrem bestätigten Fortschritt wieder aufgenommen. Ein laufender nativer ExifTool-Schreibschritt darf nicht gewaltsam in der Mitte der Transaktion beendet werden.
5. **Offline ist kein Löschen.** NAS-/VPN-Nichterreichbarkeit rechtfertigt kein Entfernen katalogisierter Fotos.
6. **Stabilität vor Eleganz.** Keine Änderung einer funktionierenden Funktion oder des Layouts ohne fachlichen Grund und Abstimmung.

## Komponenten und Eigentümer

| Zuständigkeit | Aktuelle Hauptkomponente |
| --- | --- |
| Windows-Oberfläche, Benutzeraktionen | `MainWindow.xaml`, thematisch getrennte `MainWindow.*.cs`-Dateien |
| Galerie-Layout/Hit-Testing/Abschnitte | `Controls/BrowseGallerySurface.cs` |
| Index, sichtbare Thumbnail-Pages | `Data/BrowseCacheRepository.cs`, `Services/BrowseThumbnailPageCache.cs` |
| Pakete, Prioritäten und Wiederaufnahme | `Services/PCasaScheduler.cs`, `Services/SchedulerTaskPackage.cs`, `Services/DurablePackageJournal.cs` |
| Ordnerwahl/Ausschlüsse/Status | `Data/FolderManagerSelectionService.cs`, `Services/FolderExclusionRules.cs`, `Services/FolderScanStatusTracker.cs` |
| Dateisystemscan und Hintergrundbeobachtung | `Services/FolderScanner.cs`, `Services/BackgroundFolderMonitor.cs` |
| Metadaten/Tags | `Data/ImageTagRepository.cs`, `Services/ImageExifReader.cs`, `Services/ExifToolKeywordWriter.cs` |
| Dateiübertragung und Wiederanlauf | `MainWindow.GalleryFileOperations.cs`, `MainWindow.DurableGalleryTransfer.cs`, `Services/DurableGalleryTransferJournal.cs` |
| Sprachverwaltung | `Services/UiLanguage.cs` und `Resources/english.json` |
| UI-Einstellungen | `MainWindow.Appearance.cs` und `%LOCALAPPDATA%\PCasa\appearance.json` |

**WPF zeichnet unmittelbar.** Aufwendige Datei-, NAS-, SQLite-, Decoder- und Cachearbeit wird koordiniert; die Benutzeroberfläche darf nicht auf einen blockierten SMB-Scan warten. Kontrollierte Fachoperationen sind über den gemeinsamen Scheduler sichtbar. Mehrere begrenzte Worker-Lanes gehören zu *derselben* Scheduler-Instanz und sind kein zweites Wartesystem.

## Paketmodell und Prioritäten

`PCasaScheduler` kennt die Prioritäten `ManualAction`, `Interactive`, `UserAction`, `Background` und `Idle` sowie getrennte Arbeitstypen, etwa `CatalogScan`, `ThumbnailRead`, `ViewerRead` und `SourceProbe`.

- Neue manuelle Pakete erhalten Vorrang; unterbrochene manuelle Arbeit wird grundsätzlich **zuletzt begonnen – zuerst fortgesetzt (LIFO)** abgearbeitet.
- `scheduler_packages` in SQLite speichert Auftragstyp, Nutzdaten, ursprüngliche Priorität, Einreihungsfolge, bestätigten Cursor und Wiederholungsinformationen. Typisierte Wiederherstellungsfabriken rekonstruieren die Arbeit nach Neustart.
- Nicht verfügbare Quellen können einen späteren Wiederholungsversuch erfordern. Die unerledigten Tag-Intentionen bleiben in `pending_tag_edits` erhalten; der Scan darf nicht aufgrund einer unerreichbaren NAS-Datei für immer blockiert werden.
- Die gemeinsame Fortschrittsprojektion zeigt den gerade tatsächlich aktiven Auftrag und aggregiert zusammengehörige Tag-Schreibvorgänge. Sie ist **kein eigener Scheduler**.
- Quellgenerationen und SQLite-Commit-Grenzen schützen vor einer verspäteten Veröffentlichung überholter Scan- oder Ordnerentscheidungen.

Die konkreten Ausführungspfade und Sicherheitsverträge stehen im Code. Der ursprüngliche [Scheduler-Entwurf](operation-scheduler-design.md) ist ausdrücklich nicht in allen frühen Konzeptdetails 1:1 realisiert.

## Daten- und Bildfluss

1. Ordnermanager und Dateisystemscan definieren überwachte Wurzeln und katalogisierte Bilder.
2. EXIF, GPS, Kamera, Tags, Pfade und Zustände werden lokal in SQLite gehalten.
3. Thumbnails liegen in lokalen Dateien und in mehrstufigen gepackten Browse-Caches; SQLite hält Referenzen und Gültigkeitsinformationen.
4. Die virtuelle Galerie erhält einen geordneten Index und lädt Bilddaten nur für benötigte Pages. Scrollen durch katalogisierte Bilder soll keinen erneuten NAS-Scan auslösen.
5. Tag-Änderungen werden zunächst als dauerhaftes lokales Soll protokolliert; ExifTool schreibt sie in die Originaldatei und bestätigt anschließend die Intention.
6. Nach Verschieben/Kopieren/Entfernen aktualisieren die bestehenden Fachpfade Katalog, Cache- und Galerieansicht; kein pauschaler neuer EXIF-/Thumbnail-Lauf für bereits bekannte verschobene Daten.

**Cache-Vorschau im Fortschrittsfenster:** Tag-Jobs verwenden zunächst gepackte lokale Thumbnail-Daten und bei fehlendem Pack-Eintrag den vorhandenen lokalen Einzel-Thumbnail. Das NAS-Original wird nicht nur für diese Vorschau dekodiert.

## Grenzen

Die **optionale zentrale Mehrclient-Synchronisierung** ist als Ziel konzipiert, aber im aktuellen Hauptfenster nicht eingerichtet; der Ordnermanager wird derzeit mit `serverDatabaseConfigured: false` geöffnet. Eine echte Server-Synchronisierung darf daher nicht als implementierte Funktion beschrieben werden. Ebenso fehlen Bildbearbeitung, eine interaktive GPS-Karte und eine externe Plugin-/Werkzeugverwaltung. Der Viewer und GPS-Metadatenanzeige sind davon zu unterscheiden.

## Qualität und Freigabe

Der Workflow [Scheduler Consistency Tests](https://github.com/jjs-hamburg/PCasa/actions/workflows/scheduler-consistency-tests.yml) testet Windows-Katalog-/EXIF-/Tag-, echte WPF-, Ordner-, Dateioperations- und Wiederanlaufszenarien und baut `PCasa-win-x64`. Beispiel: [erfolgreicher Lauf für Commit `0e621da0`](https://github.com/jjs-hamburg/PCasa/actions/runs/40765826666). Das ist **keine** Behauptung vollständiger Prüfung des echten NAS/VPN-Bestands oder eine automatische Freigabe nach `main`.
