# PCasa – Lizenzprüfung der Windows-Distribution

Stand: 9. November 2026 – Alpha.7 in Prüfung.

**Wichtig:** Die automatische CI-Prüfung beweist die Anwesenheit der benannten
Lizenzdateien, aber nicht die vollständige juristische Konformität aller darin
enthaltenen Komponenten. Ein grüner Build ist kein Rechtsgutachten.

## Eigener Code
- PCasa: Copyright (c) 2026 Jens-Jürgen Schäfer.
- Lizenz: GPL-3.0-only (Datei `LICENSE` im Repository;
  `Licenses/PCasa-GPL-3.0.txt` im Setup).
- Der Tag des Releases verweist auf den konkreten Quellcode einschließlich
  Buildskripten: https://github.com/jjs-hamburg/PCasa/releases

## Mitgelieferte Abhängigkeiten

| Komponente | Version | Lizenz- und Herkunftsnachweis |
| --- | --- | --- |
| Microsoft.Data.Sqlite | 10.0.12 | MIT; NuGet- und transitive notices im Paket |
| CefSharp.Wpf.NETCore | 152.0.100 | BSD; CEF- und Chromium-Lizenzhinweise erforderlich |
| NetVips | 3.2.0 | MIT |
| NetVips.Native.win-x64 | 8.18.7 | LGPL-3.0-or-later, weitere native Codec-Lizenzen |
| MapLibre GL JS | 5.9.0 | BSD-3-Clause, `Licenses/MapLibre-LICENSE.txt` |
| ExifTool | 13.59 | GPL/Artistic und Perl-Bundle-Lizenzen |
| .NET 10 Desktop Runtime | separat | Microsofts offizieller Installer; nicht im PCasa-Setup eingebettet |
| Inno Setup | Buildzeit | Erzeugt den Installer; kein eingebetteter Inno-Setup-Quellcode |

## Tatsächliche technische Sicherungen

- [x] GPL-Text und eigene Copyright-Angabe im Repository.
- [x] GPL-, LGPL-, MIT-, Apache- und MapLibre-Texte im Publish-Paket.
- [x] Third-Party-Übersicht, tatsächliche NuGet-Paketliste und vorhandene
  Original-NuGet-Lizenzdateien werden in `Licenses/` gesammelt.
- [x] Für ExifTool werden die Lizenzdatei und das Strawberry-Perl-Lizenzarchiv
  aus dem geprüften Windows-Bundle mit dem EXE übernommen.
- [x] Fehlende grundlegende Lizenzdateien stoppen den Build.
- [x] Ab Alpha.7 prüfen Installationstests BEIDE Setup-Modi anhand des
  tatsächlichen Installationsverzeichnisses auf die Pflichtdateien.
- [x] Ab Alpha.7 öffnet Hilfe → About den lokalen `Licenses`-Ordner;
  `Licenses/README.txt` beschreibt den Inhalt.
- [x] PCasa bundelt keine Microsoft .NET Desktop Runtime mehr.
- [x] Release-Erstellung aus `main` erst nach bestandenem Windows-Build
  (ab Alpha.7).

## Was sich nicht durch bloße Dateiexistenz beweisen lässt

- [ ] Vollständige Prüfung aller Chromium/CEF-Hinweise der **exakt**
  ausgelieferten Version einschließlich weiterverteilten nativen Komponenten.
- [ ] Vollständige Zuordnung aller enthaltenen libvips-Codecs zu deren
  jeweiligen Lizenz- und Copyright-Anforderungen. Die upstream
  `THIRD-PARTY-NOTICES.md` liegt im NuGet-Bundle, ist aber keine eigene
  durchgeführte Binär-zu-Lizenz-Auditierung.
- [ ] Prüfnachweis für die LGPL-Bedingungen, insbesondere Ersetzbarkeit
  der DLLs, Quelle/Änderungen und Weitergabebedingungen.
- [ ] Vollständige rechtliche Beurteilung der ExifTool-/Perl-Distribution,
  einschließlich der Bestandteile im Perl-Lizenzarchiv.
- [ ] Finale unabhängige juristische Prüfung vor einer vorbehaltlosen
  öffentlichen Konformitätsbestätigung.

Diese Punkte sind **keine festgestellten Verstöße**. Sie dürfen jedoch auch
nicht stillschweigend als abgeschlossen markiert werden.

## Quell- und Lizenzreferenzen

- https://cefsharp.github.io/
- https://chromiumembedded.github.io/cef/general_usage.html
- https://www.nuget.org/packages/NetVips.Native.win-x64/8.18.7
- https://github.com/kleisauke/libvips-packaging/releases/tag/v8.18.7
- https://github.com/libvips/libvips/releases/tag/v8.18.7
- https://exiftool.org/
- https://www.gnu.org/licenses/gpl-3.0.html

`THIRD-PARTY-NOTICES.md` und `tools/collect_third_party_licenses.ps1`
dokumentieren die tatsächlich gepackten Nachweise.
