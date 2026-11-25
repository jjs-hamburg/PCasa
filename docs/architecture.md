# PCasa – Architektur Alpha.63

**Stand 25.11.2026 · Entwicklungszweig `alpha63-architecture`, PR #64.** Testkandidat, keine Freigabe nach main.

## Schichten

| Komponente | Zielplattform | Zuständigkeit |
| --- | --- | --- |
| PCasa.Core | .NET 10 | SQLite, Katalog, Scan- und Quellzustände, Scheduler, Jobjournal, Inventur, Dateioperationen, ExifTool, Tags/GPS, Datenbankwartung |
| PCasa.Imaging | .NET 10 + libvips | RAW-/PSD-Dekodierung, Thumbnail-Erzeugung, Pack-Speicherung und -Lesen, Cache-Rebuild, portable Import-Komposition |
| PCasa.App | .NET 10 / WPF / Windows x64 | Oberfläche, Darstellung, Interaktionsadapter, Windows-Video, JPEG-/SMB-Optimierungen, native Windows-Integration |

Abhängigkeiten: App → Imaging/Core, Imaging → Core, nie umgekehrt. Bestehende Quellen werden teilweise noch über Compile-Links eingebunden, jedoch nur einmal in der zuständigen Assembly kompiliert. Der Core benötigt kein WPF und keine Bildbibliothek. IFolderImportMedia und IImportThumbnailStore trennen Importsteuerung von Dekodierung.

## Architekturregeln

- Eine fachliche Operation besitzt eine Identität und einen Ausführungspfad. Worker-Lanes sind keine zusätzlichen Scheduler.
- Quellgenerationen und SQLite-Commit-Grenzen schützen vor verspäteten Scan-Commits. Ordner- und Dateifortschritt bleiben auch beim Entfernen eines Einleseauftrags erhalten.
- Originale sind die Autorität für unterstützte Tag- und EXIF-Schreibvorgänge. RAW-GPS bleibt als Katalog-Override gespeichert. Keine Sidecars.
- Galerie und Viewer benutzen unabhängige, begrenzte Lesedienste. Zeichnen und Navigation warten nicht auf die Import-Warteschlange.
- Katalog, Metadaten und bestehende Thumbnail-Dateien bleiben beim Upgrade erhalten; kein pauschaler Neuimport.
- Datenbankwartung außerhalb der fachlichen Warteschlange; ausschließlich manuelle Sicherung einschließlich Vorschaudaten.

## Scheduler

Der [Scheduler-Vertrag](taskmanager-scheduler-contract.md) ersetzt den früheren P0–P4-Vertrag. Fachlich P0 manuelle Mehrfach- und Einleseaufträge, P1 automatische Bibliotheksaktualisierung, P2 Leerlaufarbeiten. Alte numerische Enum-Werte bleiben als technische Kompatibilitätswerte erhalten.

Einzelaktionen und Datenbankwartung sind keine wiederanlaufenden Scheduler-Jobs. Konflikte mit Einlesen werden an sicheren Grenzen koordiniert. Nach Neustart bleiben Einleseabsichten erhalten. Nicht abgeschlossene Dateioperationen dürfen nicht automatisch erneut Originale verändern. Sicherheits- und Reconciliation-Receipts sind keine neuen Benutzeraufträge.

## Kontinuierlicher Bestandscheck

Eine permanente Taskmanager-Zeile; global pausierbar, nicht löschbar oder verschiebbar. Die Pause bleibt über Neustart erhalten. Erreichbare vollständig eingelesene Wurzeln werden in gespeicherter Prüfsequenz durchlaufen. Keine 30-Tage-Frist; Retry-Fristen dienen ausschließlich Erreichbarkeit und Fehlerbehandlung. Die Inventur vergleicht Dateifingerabdrücke statt Bildpixel. Erkannte Änderungen gehen in die reguläre Einleseverarbeitung. Offline ist kein Löschen.

## Grenzen und Abnahme

Windows: Katalog-, Scheduler-, WPF-Smoke- und Paketprüfungen. Linux/macOS: Core-Regressionen und reale libvips-/ExifTool-Tests (JPEG, PNG, PSD, Pack-Cache). Noch keine Linux-/macOS-Oberfläche oder native Video-Frame-Implementierung. Case-sensitive Dateisystemidentität und NAS-Mount-Zuordnung müssen vor einer nativen Desktopfreigabe gesondert geprüft werden; die vorhandene SQLite-Pfadidentität ist weitgehend case-insensitive.

Alte SQL-Kompatibilitätsobjekte und Rezeptnamen werden nicht ohne überprüfte Migration entfernt. Die reale Upgrade-/NAS-/VPN-Abnahme, insbesondere Ordnerwechsel, Joblöschung, Beenden und Offline/Online, bleibt erforderlich. CI allein ist keine Produktfreigabe. Der Alpha.63-Installer kann als Pull-Request-Artefakt geprüft werden; eine Veröffentlichung erfolgt ausschließlich nach Merge auf main.
