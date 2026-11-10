# PCasa – Lizenzprüfung der Windows-Distribution

Stand: 9. November 2026 – Alpha.8-Lizenzpaket in Prüfung; veröffentlichte Basis Alpha.7.

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

- [x] Chromium/CEF: Der Windows-Build exportiert die **vollständige, intern
  erzeugte** Drittanbieter-Credits-Seite direkt aus der tatsächlich verwendeten
  CEF-Laufzeit (19.512.304 Byte im geprüften Windows-Payload). Außerdem liegen
  die Original-CEF/CefSharp-Lizenzen vor; der Hash von libcef.dll ist gepinnt.
- [x] libvips: Der Hash von libvips-42.dll ist gepinnt, die 28
  versionierten Build-Abhängigkeiten samt Quell- und Buildlinks sind unter
  `Licenses/NATIVE-BUILD-VERSIONS.txt` dokumentiert; die Original-Notices,
  LGPLv3 und Cairo-MPL-1.1-Texte liegen im lokalen Lizenzordner.
- [ ] Vollständige Prüfung der Copyright-Originaltexte und
  der korrespondierenden Quellcodeverfügbarkeit sämtlicher **statisch
  eingebauter** libvips-Abhängigkeiten.
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


## Ergänzende Binärprüfung (9. November 2026)

Der auf Windows erzeugte Inventurlauf für Commit `3fc22cb` wurde erfolgreich
ausgeführt (GitHub Actions Run 42681145475). Er erfasste **780** veröffentlichte
Dateien und **12** aufgelöste NuGet-Pakete, einschließlich transitiver Pakete.
Die tatsächlich veröffentlichte Alpha.7-EXE wurde dabei nicht entpackt;
erfasst wurde der aus demselben Quellstand erzeugte Windows-Publish-Payload.
Deshalb sind die SHA-256-Hashes noch kein Byte-für-Byte-Nachweis des
bereits veröffentlichten Installers.

Faktisch im Publish-Payload enthalten waren:
- `libcef.dll` und die Paket-Lizenzdateien zu CefSharp 152.0.100 sowie
  chromiumembeddedframework.runtime 152.0.10;
- `libvips-42.dll`, LGPL-3.0 und das Paketdokument
  `NetVips.Native.win-x64-8.18.7/THIRD-PARTY-NOTICES.md`;
- ExifTool-13.59-Programmdateien sowie `exiftool_files/LICENSE` und
  `exiftool_files/Licenses_Strawberry_Perl.zip`.

Die NuGet-Inventur weist fünf Pakete mit Paket-Lizenzdatei und weitere
Pakete mit MIT, Apache-2.0 beziehungsweise LGPL-3.0-or-later aus.
Die Paketangaben sind Lizenzmetadaten, noch keine eigene Bewertung
der jeweiligen Redistribution-Pflichten.

Ab PR #1 wird außerdem vor dem Installer-Bau gegen das **tatsächliche
Publish-Verzeichnis** geprüft: benannte Lizenztexte, native Laufzeitdateien,
NuGet-Metadaten und das Vorhandensein von Lizenzdokumenten im
Strawberry-Perl-Archiv. Das Ergebnis wird in
`component-evidence.csv` dokumentiert.

### Warum die rechtliche Konformität noch nicht bestätigt ist

Die noch erforderlichen Prüfungen sind **inhaltlich**, nicht nur technisch:
1. libvips: Copyright-Originaltexte und korrespondierende Quellen aller
   tatsächlich statisch verlinkten Abhängigkeiten vollständig zuordnen.
2. ExifTool/Perl: den Inhalt aller mitgelieferten Lizenzdateien den
   konkreten Perl-Modulen und nativen DLLs zuordnen.
3. Installer: die publizierte Setup-EXE selbst entpacken und die Hashes
   mit dem verifizierten Publish-Payload abgleichen.

Diese Prüfungen sind keine festgestellten Verstöße. Der vorliegende
Automatismus prüft notwendige Voraussetzungen, keine rechtsverbindliche
Freigabe. Ein erfolgreicher Build darf dieses Kapitel nicht als
abgeschlossen darstellen.


## Zusätzliche Windows-Belege (Alpha.8-Vorbereitung)

- Der neue CEF-Export liefert 19.512.304 Byte in
  `Licenses/Chromium-Credits.html`; die Datei wurde anhand ihrer SHA-256 im
  Windows-Publish-Inventar erfasst. Der Inhalt stammt aus
  `chrome://credits/` derselben CEF-Laufzeit, mit der die Software gebaut wird.
- `libcef.dll` SHA-256:
  `08a40295bebd9421468115a09c375f3decce219b6673ff77410c479c03b43603`.
- `libvips-42.dll` SHA-256:
  `06dab07cc386748513337a31672b1d5269fbc770a14ff403080575665bf0813f`.
- Die offizielle Mozilla MPL-1.1-Datei wurde im erfolgreichen Windows-Test
  mit 25.755 Byte und SHA-256
  `f849fc26a7a99981611a3a370e83078deb617d12a45776d6c4cada4d338be469`
  in `Licenses/MPL-1.1.txt` gefunden.
- Die native Buildversion ist dem Upstream-Tag v8.18.7 zugeordnet,
  einschließlich der tatsächlichen statischen Windows-Variante.

Diese Belege sind reproduzierbare Befunde aus dem geprüften Windows-Publish.
Ein grüner Test dieses Payloads ersetzt nicht die Kontrolle der tatsächlichen
Installer-Dateien oder eine Prüfung sämtlicher Lizenzpflichten.


## Prüfung des tatsächlich installierten Lizenzordners (9. November 2026)

Vom PCasa-Anwender wurde der installierte Ordner als `licenses.zip` bereitgestellt.
Das Archiv enthält **18 Dateien** (Verzeichniseinträge nicht mitgezählt).
Diese Stichprobe weist die **lokale Installation** der Lizenzunterlagen nach,
nicht den vollständigen Inhalt der Setup-EXE.

- `Chromium-Credits.html`: 19.512.304 Byte; SHA-256
  `822ae39dd655639470d292bd69db6616bfb56ae364f70bde76067d15543a1fe0`.
- `MPL-1.1.txt`: 25.755 Byte; SHA-256
  `f849fc26a7a99981611a3a370e83078deb617d12a45776d6c4cada4d338be469`.
  Der Wert stimmt mit dem zuvor dokumentierten Windows-Payload überein.
- `NATIVE-BUILD-VERSIONS.txt`: 2.555 Byte; SHA-256
  `8d1ea0875150ac2b86c9972193203b858cc30f263d4566cf06286cea7b19b6c8`.
  **Korrektur erforderlich:** Die Überschrift sagt noch „Alpha.7“;
  die Liste ist für den Alpha.8-Lizenzstand dokumentiert.
- Native Original-Notices: `Packages/NetVips.Native.win-x64-8.18.7/THIRD-PARTY-NOTICES.md`.
  Ihre Auflistung nennt u. a. `fribidi`, `glib`, `libexif`, `libheif`,
  `librsvg`, `pango`, `proxy-libintl` sowie `libvips` unter LGPLv3.
- `exiftool/exiftool_files/LICENSE` und
  `exiftool/exiftool_files/Licenses_Strawberry_Perl.zip` sind laut
  `Licenses/README.txt` **außerhalb** des Licenses-Ordners vorgesehen.
  Ihr Vorhandensein kann aus diesem Teilarchiv nicht geprüft werden.

**Ergebnis:** CEF-Credits und die zentralen Texte sind lokal nachgewiesen.
Keine abschließende Freigabe für das native Windows-Static-Bundle:
Die Upstream-Beschreibung von `kleisauke/libvips-packaging` erläutert,
dass die Windows-Ausgabe die statische „web“-Distribution von
`libvips/build-win64-mxe` weiterverarbeitet. Für die darin verbundenen
LGPL-Bibliotheken muss die korrespondierende Quelle und die Möglichkeit,
geänderte LGPL-Komponenten nach den jeweils einschlägigen Bedingungen
neu einzubinden, noch konkret nachgewiesen oder die Bibliotheksverteilung
technisch umgestellt werden. Eine bloße Ersetzung von `libvips-42.dll`
beweist diese Bedingung für statisch enthaltene Bibliotheken nicht.

Geprüfte Upstream-Herkunft:
- https://github.com/kleisauke/libvips-packaging/tree/v8.18.7
- https://github.com/libvips/build-win64-mxe/releases/tag/v8.18.7

Diese offene Feststellung wird ausdrücklich **nicht** durch einen grünen
Dateiexistenztest aufgehoben.


## Eingesammelte feste Originaldokumente (10. November 2026)

Der Quellstand `license-static-verification` archiviert unter
`license-baseline/` **43 feste Original- und Provenienzdateien**:

- `libvips-8.18.7/`: Upstream-Lizenz, exakt versionierte Build- und
  Drittanbieter-Notices, die Copyright-/Lizenztexte der 29 erfassten
  Bibliotheken und zusätzliche LLVM-Laufzeit-Lizenztexte für die zwei
  gesonderten DLLs `libc++.dll` und `libunwind.dll`.
- `exiftool-13.59/`: Original-LICENSE und README aus dem
  ExifTool-Tag `13.59`, sowie der genaue Herkunfts- und Lizenzarchiv-Nachweis.
- Die Dateien werden beim normalen Publish unverändert unter
  `Licenses/Native/` installiert und gegen den im Git-Repository
  versionierten Originalbestand per SHA-256 verglichen.
- Der bisherige erneute Mozilla-MPL-Download entfällt. Für künftige
  Releases wird die originale `COPYING-MPL-1.1` aus Cairo 1.18.6
  verwendet (SHA-256 `fa01277004aff314888151ea523bdf390992892e13523984f221695d48c7455b`).
  Das ist nicht die bytegleiche Datei der bereits veröffentlichten Alpha.8.
- Die ExifTool-Windows-Distribution bleibt unverändert; das mitgelieferte
  `Licenses_Strawberry_Perl.zip` (SHA-256
  `8bd5fc8dc0ef3b96881ddad0974b79e86f4043f76ab318a730b084adf0890bd0`)
  enthält 153 Dateien einschließlich 63 Lizenz-/NOTICE-Kandidaten.
  Der vollständige Inhalt wird beim Build gehasht und inventarisiert.
- Die dynamische libvips 8.18.7 wurde mit NetVips 3.2.0 unter Windows
  erfolgreich funktionsgeprüft (38 native DLLs). Sie ersetzt die
  bislang produktiv ausgelieferte statische NuGet-Bibliothek noch nicht.

**Grenze:** Sammlung und Mitlieferung aller bekannten Originaldokumente
ist nicht identisch mit dem Nachweis aller Lizenzpflichten.
Insbesondere sind die vollständigen korrespondierenden Quellen und
Austausch-/Relinking-Bedingungen des noch produktiv ausgelieferten
statischen libvips-Pakets nicht erledigt; außerdem ist die genaue
LLVM-Toolchain-Revision für die dynamische Alternative noch zuzuordnen.
Die Perl-Lizenzdateien sind vollständig im Originalarchiv enthalten,
aber die Zuordnung sämtlicher betroffener Perl-Binärbestandteile ist
nicht unabhängig abschließend bestätigt. Dies sind die **konkreten**
noch offenen technischen Nachweise, keine fehlenden allgemeinen
GPL- oder LGPL-Mustertexte.
