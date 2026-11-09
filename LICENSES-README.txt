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
libvips: LGPL-3.0.txt, 38 einzeln ersetzbare DLLs mit SHA-256-Werten
in libvips-dynamic-files.csv, komponentenweise Lizenz- und Quellnachweise
in libvips-component-licenses.csv, zugehörige LLVM-Laufzeitlizenzen in
LLVM-LICENSE.txt, sowie die originalen Copyright- und Lizenztexte unter
Native-Original-Texts/ (20 Dateien mit Herkunftsangaben) und die Hinweise unter Packages/ und die Quellcode-
und Build-Verweise in THIRD-PARTY-NOTICES.md.
ExifTool und das mitgelieferte Strawberry Perl:
../exiftool/exiftool_files/LICENSE und
../exiftool/exiftool_files/Licenses_Strawberry_Perl.zip.

Die .NET 10 Desktop Runtime wird über den offiziellen Microsoft-Installer
separat bezogen und ist nicht Teil der PCasa-Binärdateien.

Die Lizenztexte sind Bestandteil des Windows-Installers und bleiben nach
der Installation neben PCasa.App.exe unter Licenses/ zugänglich.
