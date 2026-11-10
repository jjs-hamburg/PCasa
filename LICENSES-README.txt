PCasa – Lizenzen und Hinweise zu Fremdkomponenten
=================================================

PCasa: Copyright (c) 2026 Jens-Jürgen Schäfer
Lizenz: GNU General Public License, Version 3 (GPL-3.0-only).
Vollständiger Text: PCasa-GPL-3.0.txt

Die PCasa-Quelldateien und Buildskripte zur jeweiligen veröffentlichten
Version stehen unter https://github.com/jjs-hamburg/PCasa bereit.
Die Versionskennung steht unter Hilfe > About und in BUILD-INFO.txt.
GitHub-Releases: https://github.com/jjs-hamburg/PCasa/releases

FREMDE KOMPONENTEN
------------------
Die Komponenten behalten ihre jeweils eigenen Lizenzen. Eine Übersicht
mit Herstellern, Versionen, Quellcode-Links und Hinweisen findet sich in
THIRD-PARTY-NOTICES.md. Die Originaltexte der NuGet-Pakete sind unter
Packages/ abgelegt. NuGet-license-inventory.csv enthält ein Inventar der
tatsächlich aufgelösten NuGet-Pakete.

Details zu CEF/Chromium und libvips einschließlich LGPL-Austauschbedingungen:
CEF-LIBVIPS-GUIDE.txt.

CEF/Chromium: die offline bereitgestellte Chromium-Credits.html wird beim
Windows-Build aus genau der verwendeten CEF-Version erzeugt.
CEF/Chromium: Lizenzdateien unter Packages/ sowie Chromium-interne
Lizenz- und Copyright-Hinweise (about:license bzw. about:credits).
libvips: LGPL-3.0.txt, die Hinweise unter Packages/ und die Quellcode-
und Build-Verweise in THIRD-PARTY-NOTICES.md.
ExifTool und das mitgelieferte Strawberry Perl:
../exiftool/exiftool_files/LICENSE und
../exiftool/exiftool_files/Licenses_Strawberry_Perl.zip.

Die .NET 10 Desktop Runtime wird über den offiziellen Microsoft-Installer
separat bezogen und ist nicht Teil der PCasa-Binärdateien.

FESTE QUELLTEXTE / ORIGINALHINWEISE (STAND 8.18.7 / 13.59)
-------------------------------------------------------------
Im installierten Unterordner Native/libvips-8.18.7/ liegen die
versionsgebundenen Original-Lizenztexte und Hinweise für libvips und
29 erfasste Komponenten einschließlich libnsgif. Native/exiftool-13.59/
enthält die Original-Lizenz und README von ExifTool 13.59 sowie die
Herkunftsnachweise zum vollständigen Strawberry-Perl-Lizenzarchiv.
Diese Quellen werden aus dem PCasa-Repository übernommen, nicht bei
jedem Release erneut aus dem Internet geladen.

Die Originalarchive von ExifTool/Strawberry Perl liegen weiterhin bei
exiftool/exiftool_files/ (siehe oben); die Dokumentation im Ordner
Native/ ist kein Ersatz für dieses Lizenzarchiv.
Für LGPL-Bibliotheken sind die separaten Anforderungen zur
Quellcodeverfügbarkeit und Austauschbarkeit zu beachten. Das Vorliegen
eines Lizenztextes allein erfüllt diese nicht.

Die Lizenztexte sind Bestandteil des Windows-Installers und bleiben nach
der Installation neben PCasa.App.exe unter Licenses/ zugänglich.
