# PCasa

**PCasa** ist ein lokaler Fotoverwalter für **Windows x64**, inspiriert von der übersichtlichen Bedienung von Picasa und ausgelegt auf große Fotoarchive.

> **Wichtiger Hinweis (29.10.2026):** Der Standardzweig **main** enthält einen **älteren technischen Ausgangsstand**. Die laufende Entwicklung und die aktuell gepflegte Dokumentation befinden sich im Zweig **[scheduler-consistency](https://github.com/jjs-hamburg/PCasa/tree/scheduler-consistency)**. Die hier genannten aktuellen Funktionen beziehen sich **nicht** auf den alten main-Code.

## Aktueller Entwicklungsstand

Der aktive Zweig enthält eine **WPF-/ .NET-10-Anwendung** mit:

- durchgehend scrollbarer, virtualisierter Galerie, Ordnerbaum und Ordnermanager;
- einstellbarer Thumbnailgröße und -abständen, mehreren Designs und Galerie-Sortierung pro Ordner;
- Tag- und Dateinamensuche einschließlich NOT, Suchbereichen und Sichtbarkeitsfilter;
- Bildbetrachter, Mehrfachauswahl, Datei-Kopieren/Verschieben und EXIF-/GPS-/RAW-Anzeige;
- dauerhaft gespeicherten Tag-Schreibaufträgen mit ExifTool, zentralem Scheduler, priorisierten Paketen und Wiederaufnahme nach Neustart;
- einer zentralen **Deutsch/Englisch-Sprachverwaltung** mit Umschaltung im laufenden Betrieb.

**Noch nicht implementiert:** Bildbearbeitung, zentrale Mehrclient-Synchronisierung, GPS-Karte und die besprochene externe Werkzeug-/Plugin-Anbindung (z. B. Autopano Giga). Das Ziel von mindestens 500.000 Bildern und die Robustheit mit realem NAS/VPN erfordern weiterhin Tests im Zielsystem.

## Zum aktuellen Code, Build und zur Dokumentation

- **[Aktuelle README und Einstieg](https://github.com/jjs-hamburg/PCasa/blob/scheduler-consistency/README.md)**
- [Anforderungen und Funktionsstand](https://github.com/jjs-hamburg/PCasa/blob/scheduler-consistency/docs/requirements.md)
- [Architektur](https://github.com/jjs-hamburg/PCasa/blob/scheduler-consistency/docs/architecture.md)
- [Galerie und Bedienung](https://github.com/jjs-hamburg/PCasa/blob/scheduler-consistency/docs/gallery.md)
- [Scheduler und Wiederherstellung](https://github.com/jjs-hamburg/PCasa/blob/scheduler-consistency/docs/operation-scheduler-design.md)
- [Datenhaltung](https://github.com/jjs-hamburg/PCasa/blob/scheduler-consistency/docs/database.md)
- [Technik und Build](https://github.com/jjs-hamburg/PCasa/blob/scheduler-consistency/docs/technology.md)
- [Sprachen](https://github.com/jjs-hamburg/PCasa/blob/scheduler-consistency/docs/localization.md)
- [Synchronisierung: Konzept, noch nicht implementiert](https://github.com/jjs-hamburg/PCasa/blob/scheduler-consistency/docs/sync.md)

**[Windows-Testpaket (`PCasa-win-x64`) in GitHub Actions](https://github.com/jjs-hamburg/PCasa/actions/workflows/scheduler-consistency-tests.yml)**: Den letzten erfolgreichen Lauf des Branches öffnen und das Artefakt herunterladen. Ein CI-grüner Windows-Build ist ein **Testpaket**, keine pauschale Produktionsfreigabe.

## English

PCasa is a Windows photo manager inspired by Picasa. **Current work is in the [scheduler-consistency branch](https://github.com/jjs-hamburg/PCasa/tree/scheduler-consistency), not in the older main baseline.** The development build includes a virtualized gallery, folder management, metadata/tagging, a durable scheduler and German/English UI. Photo editing, multi-client sync, a GPS map and external plugins are not implemented yet.
