# PCasa

**PCasa ist ein lokaler Fotoverwalter für Windows, der die Arbeitsweise von Picasa aufgreift und auf große Bildbestände auslegt.** Der Schwerpunkt liegt derzeit auf schneller Galerie, Ordnerverwaltung, Tagging und einer konsistenten Aufgabenverwaltung – nicht auf Bildbearbeitung.

> **Entwicklungsstand: 29.10.2026.** Der aktive Entwicklungszweig ist [`scheduler-consistency`](https://github.com/jjs-hamburg/PCasa/tree/scheduler-consistency). Der Standardzweig `main` enthält einen älteren Ausgangsstand. Die unten beschriebenen Funktionen beziehen sich auf **`scheduler-consistency`**. CI-grüne Builds sind **Test-/Abnahmepakete**, keine endgültige Freigabe für jeden NAS-/VPN-Aufbau.

## Heute implementiert

- **Windows x64, .NET 10 und WPF** mit linker Ordnerliste und einer virtuellen, über Ordnergrenzen durchscrollbaren Galerie; Zielgröße: mindestens 500.000 Bilder.
- **Ordnermanager** für überwachte Wurzeln und ausgeschlossene Unterordner; manuelle und automatische Scans, Explorer-Zugriff, Ordnererstellung sowie getrennte Aktionen für »Aus PCasa entfernen« und physisches Löschen.
- **Galerie:** Größen- und Abstandsregler, wählbare Dateinamen/Symbole, Hell/Dunkel/System, JPG-Hintergrundtexturen, Mehrfachauswahl, Drag & Drop, Kopieren/Verschieben, Ordnerstatus und Fortschrittsanzeige.
- **Sortieren innerhalb jedes Ordners** nach Name (Standard), Aufnahme-/Erstellungs-/Änderungsdatum, Dateigröße oder Dimensionen; jeweils auf- oder absteigend.
- **Suche** nach Tags oder Dateinamen, global/ab ausgewähltem Baum/im aktuellen Ordner, mit NOT und optionaler Einschränkung auf die sichtbare Galerie.
- **Tags** für einzelne und mehrere Bilder: Vorschläge per Maus oder Pfeiltasten/Enter, Hinzufügen und Entfernen; **eine bildbezogene Bearbeitungssitzung über Auswahl- und Ordnerwechsel hinweg**, gemeinsames OK/Abbruch, atomare lokale SQLite-Vormerkung und über ExifTool ausgeführte, dauerhaft abgesicherte Originaldatei-Schreibaufträge.
- **Bildbetrachter:** Navigation, Vollbild, Fit/Zoom und Verschieben des Bildausschnitts; RAW-Dateien können über unterstützte Decoder bzw. eingebettete Vorschaubilder betrachtet werden. RAW-Bearbeitung ist nicht Bestandteil dieses Standes.
- **Deutsch und Englisch** über eine zentrale Sprachverwaltung, einschließlich Umschalten ohne Neustart und persistenter Auswahl.

## Status und Grenzen

- **Umgesetzt:** zentrale Scheduler-Steuerung mit Prioritäten, dauerhaftem Paketjournal, Wiederaufnahme nach Neustart, lokalem Thumbnail-/Browse-Cache und Schutz vor veralteten Ergebnissen.
- **Noch nicht umgesetzt:** eigentliche Bildbearbeitung (der entsprechende Bereich ist derzeit Platzhalter), funktionsfähige zentrale Mehrclient-Synchronisierung, interaktive GPS-Karte und Erweiterungs-/Plugin-Schnittstelle. Die mögliche Übergabe an Autopano Giga oder andere Programme ist **eine Idee, keine vorhandene Funktion**.
- **Nicht durch CI ersetzbar:** Abnahme mit dem echten umfangreichen Bildbestand, NAS/VPN-Abbrüchen, Drittprogrammen, mehreren Monitoren und DPI-Skalierungen.

## Testpaket starten

1. Unter [GitHub Actions – Scheduler Consistency Tests](https://github.com/jjs-hamburg/PCasa/actions/workflows/scheduler-consistency-tests.yml) den neuesten **erfolgreichen** Lauf des Zweigs `scheduler-consistency` öffnen.
2. Unter **Artifacts** das Paket **`PCasa-win-x64`** herunterladen und vollständig entpacken. GitHub-Artefakte können nach ihrer Aufbewahrungsfrist ablaufen.
3. **`PCasa.App.exe`** starten. Das Testpaket ist für Windows x64 selbstständig lauffähig und enthält ExifTool im Unterordner `exiftool`.
4. Im **Ordnermanager** überwachte Ordner auswählen und mit **OK** bestätigen. PCasa schreibt beim bloßen Einlesen keine Bilder um.

**Benutzerdaten:** Lokaler SQLite-Katalog, Thumbnail-Caches und Einstellungen liegen unter dem Windows-Profil in `%LOCALAPPDATA%\PCasa`. Ein entpacktes Programmverzeichnis einfach zu ersetzen ist nicht dasselbe wie diesen Datenordner zu löschen. Vor Experimenten mit großen Beständen und physischem Löschen trotzdem eine Datensicherung vorhalten.

**Build aus Quelltext:** `dotnet publish src/PCasa.App/PCasa.App.csproj -c Release -r win-x64 --self-contained true` (Windows mit .NET-10-SDK; für ein vollständiges Testpaket wird ExifTool wie im GitHub-Workflow beigelegt).

## Dokumentation

- [Funktionen und noch offene Anforderungen](docs/requirements.md)
- [Bedienung der Galerie](docs/gallery.md)
- [Architektur und Zuständigkeiten](docs/architecture.md)
- [Scheduler und Paketwiederherstellung](docs/operation-scheduler-design.md)
- [Lokale Datenhaltung](docs/database.md)
- [Technik und Builds](docs/technology.md)
- [Deutsch/Englisch](docs/localization.md)
- [Geplantes Synchronisierungskonzept](docs/sync.md)
- [Testplan und Grenzen](docs/scheduler-consistency-test-plan.md)

Die Dokumente `docs/architecture-review-pr14.md` und die älteren Abschnitte des Scheduler-Entwurfs enthalten **historische Analysen**. Für die aktuelle Implementierung sind Code, Tests und die aktualisierten Statusabschnitte maßgeblich.

## English summary

PCasa is a **Windows x64 photo manager inspired by Picasa**, currently developed in the **`scheduler-consistency`** branch. It includes a virtualized continuous gallery, monitored folders, searching, persistent metadata/tag-writing jobs, image viewing, and German/English UI switching. Photo editing, central multi-client sync, an interactive GPS map and third-party plugins **are not implemented yet**. Download a successful **`PCasa-win-x64`** test artifact from the GitHub Actions workflow above; real NAS/VPN acceptance remains separate from automated tests.
