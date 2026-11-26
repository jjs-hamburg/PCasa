PCasa Metadaten-Benchmark 2 (Windows x64, portabel)
===================================================
ZIP einmal komplett auf einem lokalen Laufwerk entpacken (z.B. C:\Benchmark).
Benchmark.cmd starten und das Verzeichnis mit den JPEG-Testbildern waehlen.
Die Testbilder duerfen lokal oder auf dem NAS liegen. Nur die Bilder und
die temporaeren Testdateien befinden sich auf dem jeweiligen Laufwerk.
Engine und ExifTool laufen direkt vom lokal entpackten Paket.
Optional: Benchmark.cmd "S:\Testbilder" ohne Ordnerauswahl starten.
Keine Installation, kein PCasa-Katalog, kein Undo. Originalbilder nur gelesen.
Alle Ergebnisberichte landen im lokalen Unterordner Ergebnisse, mit
Laufwerkskennung, Zeitstempel und eindeutiger ID. Kein Bericht wird ersetzt.
Identische Bilder lokal und auf S: verwenden. Je Ordner einen Test starten.
A/B/C laufen je Bild und Operation einmal in wechselnder Reihenfolge.
MPF/HDR und weitere nicht unterstuetzte Header werden bei C separat als SKIP
gekennzeichnet. Nur PASS-Werte sind vergleichbar. Fehlgeschlagene Tests
werden nicht als Geschwindigkeitsgewinn gewertet. Katalogzeiten sind nicht
Teil dieses isolierten Dateitests. Nach Absturz ggf. nur die vom Benchmark
erzeugten .pcasa-benchmark-Ordner und PCasa-bench-data-Tempordner entfernen.
