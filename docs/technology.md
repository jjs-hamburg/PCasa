# PCasa – Technik, Build und Test

**Stand 29.10.2026 · Entwicklungszweig `scheduler-consistency`.** Dies ist der aktuelle technische Stand, keine ursprüngliche Framework-Auswahlliste.

## Laufzeit und Bibliotheken

| Bereich | Gewählte Technologie |
| --- | --- |
| Plattform | Windows x64 |
| Framework/UI | .NET 10, WPF, XAML |
| Lokale Datenbank | SQLite mit `Microsoft.Data.Sqlite`, SQL/Transaktionen ohne ORM |
| Bildverarbeitung | `NetVips` und native `NetVips.Native.win-x64` |
| Metadaten | ExifTool für EXIF/GPS und dauerhaftes Tag-Schreiben |
| Galerie | `BrowseGallerySurface` mit virtualisiertem Layout und gepackten Thumbnail-Stufen |
| Sprache | Zentrale `UiLanguage`, eingebettetes `Resources/english.json` (de/en) |

Projektdatei: [`src/PCasa.App/PCasa.App.csproj`](../src/PCasa.App/PCasa.App.csproj). Aktuelle Abhängigkeiten und Versionen werden über die Projekt-/Package-Konfiguration bestimmt.

## Bildformate und RAW

Der Scanner berücksichtigt gebräuchliche JPEG-/Bildformate und ausgewählte RAW-Formate (u. a. ARW/NEF und weitere Formate, siehe `FolderScanner.IsSupportedImagePath`). Für RAW werden, soweit technisch verfügbar, native Dekodierung oder eingebettete JPEG-Vorschauen verwendet. Orientierung/EXIF/GPS sind Bestandteil des Katalogflusses. **RAW-Bearbeitung** und allgemeine Bildbearbeitungswerkzeuge sind nicht implementiert.

## Caches

- Lokale Einzel-Vorschaubilder werden unter dem Benutzerprofil gepflegt.
- Der Browse-Cache besitzt standardmäßig drei Größenstufen **96, 192, 384** und referenziert gepackte Dateien aus SQLite.
- Sichtbare/nahe Galerie-Pages werden geladen; Scrollen soll nicht die Originalbilder über das Netzwerk neu dekodieren.
- GUI-Status und schwebendes Fortschrittsfenster sind Projektionen bestehender Scheduler-Fortschritte.

## Bauen und testen

Mit installiertem .NET-10-SDK auf Windows x64:

~~~powershell
dotnet restore src/PCasa.App/PCasa.App.csproj -r win-x64
dotnet publish src/PCasa.App/PCasa.App.csproj -c Release -r win-x64 --self-contained true -o publish/PCasa
~~~

Für das **vollständige verteilbare Testpaket** wird ExifTool benötigt. Der Workflow [`scheduler-consistency-tests.yml`](../.github/workflows/scheduler-consistency-tests.yml) beschafft die geprüfte ExifTool-Distribution, führt Tests aus, veröffentlicht einen self-contained Build und lädt das Artefakt **`PCasa-win-x64`** hoch. Es gibt einen automatischen Push-Trigger auf `scheduler-consistency` sowie einen manuellen `workflow_dispatch`-Trigger.

Testprojekte: `tests/PCasa.CatalogTests` und `tests/PCasa.WindowSmokeTests`. Zu den realen Windows-WPF-Abläufen gehören Ordnerwahl/-entfernung, Tag-Suche/-Schreiben, Kopieren/Verschieben, Sprachwechsel und lokale Tag-Job-Vorschaubilder. Beispiel: [erfolgreicher Workflow-Lauf `40765826666`](https://github.com/jjs-hamburg/PCasa/actions/runs/40765826666).

**Grenze der Tests:** Der GitHub-Windows-Runner besitzt keinen Zugriff auf das tatsächliche NAS, den VPN-Tunnel, alle persönlichen Originaldateien oder Drittprogramme. Ein grüner CI-Lauf ersetzt keine lokale Sicht-/Last-/Ausfallprüfung und keine förmliche Freigabe.

## Daten und Prozesse

Die Anwendung benötigt Schreibrechte für den lokalen PCasa-Datenordner und, beim tatsächlichen Metadaten-Schreiben, für die betreffenden Originaldateien. ExifTool-Prozesse werden über eine gemeinsame Registry verwaltet; ein Windows-Job-Object dient als zusätzliche Kill-on-Close-Sicherung. PCasa darf keine fremden ExifTool-Prozesse beenden.

## Noch offen

Server-Datenbank/Transport, Plugin-/Werkzeugintegration, Bildbearbeitung, Kartendarstellung, Installer/Updateprozess und breite reale NAS-/VPN-Abnahme sind nicht als abgeschlossene Features zu kennzeichnen.
