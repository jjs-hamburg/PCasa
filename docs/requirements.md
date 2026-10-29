# PCasa – Funktionsstand und Anforderungen

**Stand 29.10.2026 · aktiver Zweig `scheduler-consistency`.** Die Aussagen in der Spalte *Stand* beschreiben Implementierung, nicht die vollständige manuelle Abnahme am Ziel-PC.

| Bereich | Stand | Aktueller Umfang / Grenze |
| --- | --- | --- |
| Windows-Oberfläche | Implementiert | WPF/.NET 10 x64, Ordnerbaum links, virtuelle Galerie rechts; Hell/Dunkel/System und Texturen |
| Große Bildbestände | Architektur + Tests vorhanden | Ziel ≥500.000 Bilder; synthetische Tests ersetzen keinen Volltest mit echtem NAS |
| Ordnerverwaltung | Implementiert | Wurzeln, Unterordner-Ausschlüsse, OK/Abbruch, Explorer, priorisierte manuelle/automatische Scans |
| Auswahl und Dateioperationen | Implementiert | Einzel-/Bereichsauswahl, Drag & Drop, Kopieren, Verschieben, getrenntes Entfernen und Löschen |
| Suche/Sortierung | Implementiert | Tags/Dateiname, 3 Suchbereiche, NOT, Galerieansicht-Maske, 6 Sortiermerkmale je Ordner |
| Tagging | Implementiert | Mehrfachauswahl, Vorschläge per Maus/Tastatur, lokales Vormerken, ExifTool-Schreiben und Wiederherstellung |
| EXIF, Kamera und GPS | Metadaten vorhanden | Einlesen und Anzeigen von GPS-/Kameradaten; GPS-Kartenanzeige noch offen |
| Bildbetrachter | Implementiert | Navigation, Vollbild, Fit/Zoom/Pan, RAW-Vorschau abhängig von unterstütztem Format |
| Sprachen | Implementiert | Deutsch/Englisch, zentrale Laufzeitumschaltung, Einstellung wird gespeichert |
| Fortschritt und Scheduler | Implementiert | Ein Scheduler, manuelle Priorität, Paketjournal, Neustart-Wiederaufnahme, Quelle-offline-Schutz |
| Bildbearbeitung | **Nicht implementiert** | Oberfläche enthält einen Platzhalter; Crop, Retusche, Farbe, RAW-Entwicklung, Undo/Redo und Original-Backup sind Planungsanforderungen |
| Server-/Mehrclient-Synchronisierung | **Nicht implementiert** | Lokaler Katalog funktioniert ohne Server; verteilte Daten-/Thumbnail-Synchronisierung ist ein späteres Ziel |
| Externe Werkzeuge/Plugins | **Nicht implementiert** | Eventuelle Übergabe ausgewählter Bilder an Autopano Giga oder andere Anwendungen zunächst nur Entwurf |
| Release-/Installer | Testpaket verfügbar | Windows-x64-Artefakt aus CI; noch keine allgemeine Produktionsfreigabe oder Updateverwaltung |

## Verbindliche Architektur- und Bedienungsregeln

- Eine Verantwortung, ein Auftragsweg; **alle** kostspieligen Facharbeiten gehören dem zentralen Scheduler, nicht separaten UI-/Watcher-Warteschlangen.
- Manuelle Arbeit hat Vorrang; ältere manuelle Pakete behalten Priorität und Cursor. Neuere manuelle Pakete laufen vor älteren, sobald sichere Unterbrechung möglich ist.
- Dateisystem als Quelle der physischen Wahrheit, lokale SQLite-Datenbank als Katalog und dauerhafter Zustands-/Auftragsnachweis.
- Ein verlorenes NAS/VPN-Signal ist kein Beweis, dass Dateien gelöscht wurden.
- Ein vorhandenes Bild darf bei internem Verschieben nicht ohne fachliche Notwendigkeit noch einmal komplett über EXIF und Thumbnails laufen.
- Einstellungen und Benutzerdateien sollen bei Versionswechseln erhalten bleiben; keine eigenständige Sprachdatei oder zweite Settings-Verwaltung.
- Änderungen an Layout oder bestehender Funktion erfolgen nicht unbegründet und nicht als Nebenwirkung anderer Arbeiten.

## Noch geplante Bildbearbeitung

Die früher spezifizierten Werkzeuge (Beschnitt, Begradigung, Auto-Anpassung, Kontrast/Helligkeit, Retusche, Farbtemperatur, Schärfen/Weichzeichnen, Drehen/Spiegeln, Undo/Redo) bleiben **Anforderungen**, kein verfügbarer Funktionsumfang. Vor einer Umsetzung müssen RAW-Behandlung, Aufbewahrung der Originale, Backup-/Restore-Semantik und Nebenläufigkeit abgestimmt werden.

## Noch geplante Synchronisierung und externe Tools

Das Konzept sieht eine **optionale**, ausdrücklich freigeschaltete Synchronisierung von Metadaten/Thumbnails ausgewählter Netzwerkordner vor, nicht die Speicherung von Originalen in einer Serverdatenbank. Der aktuelle Client startet jedoch ohne konfigurierte Serververbindung. Details: [`sync.md`](sync.md).

Für externe Programme wäre als erster Schritt eine allgemeine, konfigurierbare Übergabe der ausgewählten **Original-Dateipfade** mit anschließendem Scheduler-gesteuertem Katalogabgleich sinnvoll. **Für Autopano Giga ist weder eine getestete Übergabeschnittstelle noch eine PCasa-Integration vorhanden.** Eine In-Process-Plugin-API ist nicht beschlossen.

## Abnahme

GitHub Actions prüft definierte Windows-Katalog-, Tag-, WPF-, Ordner- und Dateioperationsszenarien. Zusätzlich erforderlich sind Sicht- und Belastungstests auf dem Ziel-PC mit großem echtem Bestand, Netzwerkunterbrechungen, Berechtigungen und unterschiedlichen DPI-/Monitorbedingungen. Siehe [Testplan](scheduler-consistency-test-plan.md).
