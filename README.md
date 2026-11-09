# PCasa

**PCasa** ist ein lokaler Fotoverwalter für Windows x64 (.NET 10 / WPF), auf große Bildbestände ausgerichtet. Die aktuelle, im Windows-CI geprüfte Entwicklung wird über **[`main`](https://github.com/jjs-hamburg/PCasa/tree/main)** geführt. Ein Windows-Installer mit .NET-Desktop-Runtime-Prüfung und GitHub-Updatefunktion wird auf einem separaten Testzweig geprüft.

> **Stand: 8. November 2026.** Windows-Actions-Pakete sind Test- und Abnahmeversionen. CI-Erfolg ersetzt keine Abnahme mit der eigenen Bibliothek, insbesondere über NAS/Starlink/VPN.

## Funktionen

- **Galerie:** Durchgehende, virtualisierte Darstellung über Ordnergrenzen; Aktualisierung während Scans am Galerieanfang und -ende, stabile Ansicht in der Mitte; Größen-/Abstandsoptionen, Picasa-artige Geschwindigkeits-Scrollbar, Hell-/Dunkeldesign und Hintergrundtexturen.
- **Ordnerverwaltung:** Überwachte Verzeichnisse, Scans und Dateioperationen (Kopieren, Verschieben, Entfernen aus dem Katalog), Ordnervorschauen und Fortschrittsanzeige.
- **Fotos:** Mehrfachauswahl, Bildbetrachter, RAW-Vorschauen, EXIF/GPS-Informationen, interaktive GPS-Karte mit Auswahl und Gruppierung.
- **Suche und Organisation:** Sortieren pro Ordner, Tag-/Dateinamensuche und Filter, Tags und GPS zuweisen/kopieren; originale JPG-Metadaten werden in priorisierten Schreibaufträgen verarbeitet.
- **Scheduler und Taskmanager:** Zentrale Priorisierung, unterbrechbare Aufgaben, persistentes Journal, sichtbare Aufträge und Fehlerbearbeitung.
- **Datenbank:** Sicherung, sichtbare separate Wartung für Restore/Optimize/Reset und automatischer Neustart nach erfolgreicher Wartung.
- **Oberfläche:** Deutsch und Englisch, einheitliche PCasa-Dialoge.

**Noch nicht umgesetzt:** Bildbearbeitung und zentrale Mehrclient-Synchronisierung. Die App arbeitet im aktuellen Testpaket weiterhin mit Daten in `%LOCALAPPDATA%\PCasa`.

## Testpaket herunterladen

1. Unter **[GitHub Actions – Scheduler Consistency Tests](https://github.com/jjs-hamburg/PCasa/actions/workflows/scheduler-consistency-tests.yml)** einen erfolgreichen Lauf von `main` öffnen.
2. Das Artifact **`PCasa-win-x64`** herunterladen, vollständig entpacken und `PCasa.App.exe` starten.
3. Im Ordnermanager die zu überwachenden Ordner festlegen.

Das Paket enthält die benötigten Windows-x64-Komponenten und ExifTool. Bei Updates des entpackten Testpakets den **Datenordner** nicht löschen. Vorsicht bei physischem Dateilöschen und vor einem Restore eine gesonderte Sicherung behalten.

**Quelltext-Build:** `dotnet publish src/PCasa.App/PCasa.App.csproj -c Release -r win-x64 --self-contained false` mit Windows/.NET-10-SDK. Das vollständige Paket einschließlich ExifTool und Texturen wird im Workflow erstellt.

## Dokumentation

- [Funktionen und Anforderungen](docs/requirements.md)
- [Galerie](docs/gallery.md)
- [Architektur und Zuständigkeiten](docs/architecture.md)
- [Scheduler / Wiederaufnahme](docs/operation-scheduler-design.md)
- [Datenhaltung](docs/database.md)
- [Technik und Build](docs/technology.md)
- [Sprachen](docs/localization.md)
- [Synchronisierungskonzept (geplant)](docs/sync.md)
- [Testplan](docs/scheduler-consistency-test-plan.md)

Einige ältere Dokumentationsabschnitte beschreiben historische Entwicklungszustände. **Code und Regressionstests auf `main` sind für den aktuellen Stand maßgeblich.**

## English

PCasa is a Picasa-inspired Windows x64 photo organizer with a virtualized continuous gallery, GPS mapping, tagging, a durable scheduler, and database backup/restore. The current tested development baseline is **`main`**. GitHub Actions artifacts are **test builds**, not installers or final production releases. A Windows installer with per-user and all-user modes and an opt-in updater is planned but not yet implemented.
